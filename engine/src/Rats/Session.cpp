// Implements the librats 84884e7 wire protocol, copyright (c) 2026 librats
// contributors (MIT); distributed notice: licenses/Librats/librats.txt.
#include "Rats/Session.h"
#include <openssl/crypto.h>
#include <openssl/evp.h>
#include <openssl/hmac.h>
#include <algorithm>
#include <array>
#include <stdexcept>
#include <string_view>

namespace tt::rats
{
namespace
{
using Bytes = std::vector<std::uint8_t>;
using View = std::span<std::uint8_t const>;
using Bytes32 = std::array<std::uint8_t, 32>;

constexpr std::size_t tagSize = 16;
// The most that one block's body may hold.
constexpr std::size_t maxBlock = 64u << 20;
// Noise limits a message to this size, and a Rats peer rejects a larger handshake block.
constexpr std::size_t maxHandshake = 65535;
// Typed JSON is parsed on the network thread that every torrent shares, so a
// message above its bound is ignored unread. A status message holds a few
// counters; the bound for records leaves ten torrents about a thousand files
// each.
constexpr std::size_t maxStatusText = 64u << 10;
constexpr std::size_t maxBatchText = 1u << 20;
// The offer is only a size to accept. This bounds what a peer can ask the host to store.
constexpr std::int64_t maxSnapshot = std::int64_t{16} << 30;
constexpr std::int64_t progressStep = 256 << 10;
// The sender's window of 4 MiB plus slack for one chunk (64 KiB by default).
constexpr std::int64_t maxUnacknowledged = (4 << 20) + (256 << 10);
constexpr int maxBatch = 10;
constexpr std::size_t maxReason = 200;

constexpr std::uint8_t fileMessage = 4;
constexpr std::uint8_t typedMessage = 7;
constexpr std::uint8_t offerOp = 1;
constexpr std::uint8_t responseOp = 2;
constexpr std::uint8_t chunkOp = 3;
constexpr std::uint8_t endOp = 4;
constexpr std::uint8_t progressOp = 5;
constexpr std::uint8_t completeOp = 6;
constexpr std::uint8_t cancelOp = 7;

constexpr std::string_view noiseName = "Noise_XX_25519_ChaChaPoly_SHA256";
static_assert(noiseName.size() == 32);
// Binds the handshake to the Rats protocol: librats' 14-byte tag, then the
// protocol name, with no length prefix.
constexpr std::string_view prologue = "librats-proto\x1f" "rats-search/4";

[[noreturn]] void Throw(char const* message)
{
    throw std::runtime_error(message);
}

View AsBytes(std::string_view text)
{
    return {reinterpret_cast<std::uint8_t const*>(text.data()), text.size()};
}

void Put(Bytes& out, std::uint64_t value, std::size_t width)
{
    for (auto shift = width; shift > 0; --shift)
    {
        out.push_back(static_cast<std::uint8_t>(value >> ((shift - 1) * 8)));
    }
}

void Append(Bytes& out, View bytes)
{
    out.insert(out.end(), bytes.begin(), bytes.end());
}

void Append(std::vector<char>& out, View bytes)
{
    auto first = reinterpret_cast<char const*>(bytes.data());
    out.insert(out.end(), first, first + bytes.size());
}

void AppendBlock(std::vector<char>& out, View body)
{
    Bytes length;
    Put(length, body.size(), 4);
    Append(out, length);
    Append(out, body);
}

std::string Hex(View bytes)
{
    constexpr char digits[] = "0123456789abcdef";
    std::string text;
    for (auto byte : bytes)
    {
        text.push_back(digits[byte >> 4]);
        text.push_back(digits[byte & 0x0f]);
    }
    return text;
}

// A cursor over a received payload. After a read past the end every read
// returns nothing and Ok stays false.
class Reader
{
public:
    explicit Reader(View data) : data_(data) {}

    bool Ok() const
    {
        return ok_;
    }

    std::size_t Remaining() const
    {
        return data_.size();
    }

    View Take(std::uint64_t count)
    {
        if (count > data_.size())
        {
            ok_ = false;
            data_ = {};
            return {};
        }
        auto taken = data_.first(count);
        data_ = data_.subspan(count);
        return taken;
    }

    View Rest()
    {
        return Take(data_.size());
    }

    // A big-endian unsigned integer of `width` bytes.
    std::uint64_t Number(std::size_t width)
    {
        std::uint64_t value = 0;
        for (auto byte : Take(width))
        {
            value = (value << 8) | byte;
        }
        return value;
    }

private:
    View data_;
    bool ok_ = true;
};

struct Release
{
    void operator()(EVP_PKEY* key) const
    {
        EVP_PKEY_free(key);
    }

    void operator()(EVP_PKEY_CTX* context) const
    {
        EVP_PKEY_CTX_free(context);
    }

    void operator()(EVP_CIPHER_CTX* context) const
    {
        EVP_CIPHER_CTX_free(context);
    }

    void operator()(EVP_MD_CTX* context) const
    {
        EVP_MD_CTX_free(context);
    }
};

template <typename T>
using Owner = std::unique_ptr<T, Release>;

class Hasher
{
public:
    Hasher() : context_(EVP_MD_CTX_new())
    {
        if (!context_ || EVP_DigestInit_ex(context_.get(), EVP_sha256(), nullptr) != 1)
        {
            Throw("sha-256 failed");
        }
    }

    void Update(View data)
    {
        if (!data.empty() && EVP_DigestUpdate(context_.get(), data.data(), data.size()) != 1)
        {
            Throw("sha-256 failed");
        }
    }

    Bytes32 Finish()
    {
        Bytes32 digest;
        unsigned length = 0;
        if (EVP_DigestFinal_ex(context_.get(), digest.data(), &length) != 1 || length != digest.size())
        {
            Throw("sha-256 failed");
        }
        return digest;
    }

private:
    Owner<EVP_MD_CTX> context_;
};

Bytes32 Hmac(Bytes32 const& key, View data)
{
    static constexpr std::uint8_t none = 0;
    Bytes32 mac;
    unsigned length = 0;
    auto result = HMAC(EVP_sha256(), key.data(), static_cast<int>(key.size()), data.empty() ? &none : data.data(),
        data.size(), mac.data(), &length);
    if (!result || length != mac.size())
    {
        Throw("hmac failed");
    }
    return mac;
}

struct Derived
{
    Bytes32 first;
    Bytes32 second;
};

// Noise's HKDF with two outputs and no info string.
Derived Hkdf(Bytes32 const& chaining, View material)
{
    auto temporary = Hmac(chaining, material);
    std::uint8_t one = 1;
    Derived derived;
    derived.first = Hmac(temporary, View(&one, 1));
    Bytes input(derived.first.begin(), derived.first.end());
    input.push_back(2);
    derived.second = Hmac(temporary, input);
    OPENSSL_cleanse(temporary.data(), temporary.size());
    return derived;
}

Bytes32 PublicKey(EVP_PKEY* key)
{
    Bytes32 pub;
    std::size_t length = pub.size();
    if (EVP_PKEY_get_raw_public_key(key, pub.data(), &length) != 1 || length != pub.size())
    {
        Throw("x25519 failed");
    }
    return pub;
}

// The X25519 shared secret. An all-zero result comes from a low-order public
// key, which no honest peer sends, so it fails the handshake.
Bytes32 Agree(EVP_PKEY* local, Bytes32 const& remote)
{
    Owner<EVP_PKEY> peer(EVP_PKEY_new_raw_public_key(EVP_PKEY_X25519, nullptr, remote.data(), remote.size()));
    Owner<EVP_PKEY_CTX> context(EVP_PKEY_CTX_new(local, nullptr));
    Bytes32 secret{};
    std::size_t length = secret.size();
    if (!peer || !context || EVP_PKEY_derive_init(context.get()) != 1 ||
        EVP_PKEY_derive_set_peer(context.get(), peer.get()) != 1 ||
        EVP_PKEY_derive(context.get(), secret.data(), &length) != 1 || length != secret.size())
    {
        Throw("x25519 failed");
    }
    std::uint8_t bits = 0;
    for (auto byte : secret)
    {
        bits |= byte;
    }
    if (bits == 0)
    {
        Throw("x25519 produced an all-zero secret");
    }
    return secret;
}

// ChaCha20-Poly1305 with Noise's nonce: four zero bytes, then the counter in
// little-endian. Before the first key it passes data through, as Noise requires.
class Cipher
{
public:
    Cipher() = default;
    Cipher(Cipher const&) = default;
    Cipher& operator=(Cipher const&) = default;

    ~Cipher()
    {
        OPENSSL_cleanse(key_.data(), key_.size());
    }

    void Reset(Bytes32 const& key)
    {
        key_ = key;
        counter_ = 0;
        keyed_ = true;
    }

    Bytes Encrypt(View associated, View plain)
    {
        if (!keyed_)
        {
            return Bytes(plain.begin(), plain.end());
        }
        Bytes sealed(plain.size() + tagSize);
        Owner<EVP_CIPHER_CTX> context(EVP_CIPHER_CTX_new());
        auto nonce = Nonce();
        int length = 0;
        bool ok = context &&
            EVP_EncryptInit_ex(context.get(), EVP_chacha20_poly1305(), nullptr, key_.data(), nonce.data()) == 1;
        ok = ok && (associated.empty() ||
            EVP_EncryptUpdate(context.get(), nullptr, &length, associated.data(),
                static_cast<int>(associated.size())) == 1);
        ok = ok && (plain.empty() ||
            EVP_EncryptUpdate(context.get(), sealed.data(), &length, plain.data(),
                static_cast<int>(plain.size())) == 1);
        ok = ok && EVP_EncryptFinal_ex(context.get(), sealed.data() + plain.size(), &length) == 1;
        ok = ok && EVP_CIPHER_CTX_ctrl(context.get(), EVP_CTRL_AEAD_GET_TAG, static_cast<int>(tagSize),
            sealed.data() + plain.size()) == 1;
        if (!ok)
        {
            Throw("encryption failed");
        }
        ++counter_;
        return sealed;
    }

    // Throws when the data does not authenticate.
    Bytes Decrypt(View associated, View sealed)
    {
        if (!keyed_)
        {
            return Bytes(sealed.begin(), sealed.end());
        }
        if (sealed.size() < tagSize)
        {
            Throw("sealed data too short");
        }
        auto size = sealed.size() - tagSize;
        Bytes plain(size);
        Owner<EVP_CIPHER_CTX> context(EVP_CIPHER_CTX_new());
        auto nonce = Nonce();
        int length = 0;
        std::uint8_t last[tagSize];
        bool ok = context &&
            EVP_DecryptInit_ex(context.get(), EVP_chacha20_poly1305(), nullptr, key_.data(), nonce.data()) == 1;
        ok = ok && (associated.empty() ||
            EVP_DecryptUpdate(context.get(), nullptr, &length, associated.data(),
                static_cast<int>(associated.size())) == 1);
        ok = ok && (size == 0 ||
            EVP_DecryptUpdate(context.get(), plain.data(), &length, sealed.data(), static_cast<int>(size)) == 1);
        ok = ok && EVP_CIPHER_CTX_ctrl(context.get(), EVP_CTRL_AEAD_SET_TAG, static_cast<int>(tagSize),
            const_cast<std::uint8_t*>(sealed.data() + size)) == 1;
        ok = ok && EVP_DecryptFinal_ex(context.get(), last, &length) == 1;
        if (!ok)
        {
            Throw("authentication failed");
        }
        ++counter_;
        return plain;
    }

private:
    std::array<std::uint8_t, 12> Nonce() const
    {
        std::array<std::uint8_t, 12> nonce{};
        for (std::size_t i = 0; i < 8; ++i)
        {
            nonce[4 + i] = static_cast<std::uint8_t>(counter_ >> (8 * i));
        }
        return nonce;
    }

    Bytes32 key_{};
    std::uint64_t counter_ = 0;
    bool keyed_ = false;
};

struct Secured
{
    // The third handshake block's body.
    Bytes reply;
    Cipher send;
    Cipher receive;
    // The peer's static public key.
    Bytes32 remote;
};

// The initiator of Noise_XX_25519_ChaChaPoly_SHA256, revision 34:
//   -> e
//   <- e, ee, s, es
//   -> s, se
// Every payload is empty, but each message still runs EncryptAndHash on it.
class Handshake
{
public:
    Handshake() :
        local_(EVP_PKEY_Q_keygen(nullptr, nullptr, "X25519")),
        ephemeral_(EVP_PKEY_Q_keygen(nullptr, nullptr, "X25519"))
    {
        if (!local_ || !ephemeral_)
        {
            Throw("x25519 failed");
        }
        std::ranges::copy(AsBytes(noiseName), hash_.begin());
        chaining_ = hash_;
        MixHash(AsBytes(prologue));
    }

    ~Handshake()
    {
        OPENSSL_cleanse(chaining_.data(), chaining_.size());
    }

    // The first block's body: the ephemeral public key.
    Bytes32 Start()
    {
        auto message = PublicKey(ephemeral_.get());
        MixHash(message);
        EncryptAndHash(View{});
        return message;
    }

    // Reads the second block's body and finishes the handshake.
    Secured Answer(View message)
    {
        // e (32), the encrypted static key (32 + 16), the encrypted payload (16).
        constexpr std::size_t keyBytes = 32;
        constexpr std::size_t staticBytes = keyBytes + tagSize;
        if (message.size() < keyBytes + staticBytes + tagSize)
        {
            Throw("handshake message too short");
        }
        Bytes32 remoteEphemeral;
        std::ranges::copy(message.first(keyBytes), remoteEphemeral.begin());
        MixHash(remoteEphemeral);
        MixKey(Agree(ephemeral_.get(), remoteEphemeral));
        auto staticKey = DecryptAndHash(message.subspan(keyBytes, staticBytes));
        Secured secured;
        std::ranges::copy(staticKey, secured.remote.begin());
        MixKey(Agree(ephemeral_.get(), secured.remote));
        DecryptAndHash(message.subspan(keyBytes + staticBytes));
        secured.reply = EncryptAndHash(PublicKey(local_.get()));
        MixKey(Agree(local_.get(), remoteEphemeral));
        Append(secured.reply, EncryptAndHash(View{}));
        auto keys = Hkdf(chaining_, View{});
        secured.send.Reset(keys.first);
        secured.receive.Reset(keys.second);
        OPENSSL_cleanse(keys.first.data(), keys.first.size());
        OPENSSL_cleanse(keys.second.data(), keys.second.size());
        return secured;
    }

private:
    void MixHash(View data)
    {
        Hasher hasher;
        hasher.Update(hash_);
        hasher.Update(data);
        hash_ = hasher.Finish();
    }

    void MixKey(Bytes32 const& material)
    {
        auto keys = Hkdf(chaining_, material);
        chaining_ = keys.first;
        cipher_.Reset(keys.second);
        OPENSSL_cleanse(keys.second.data(), keys.second.size());
    }

    Bytes EncryptAndHash(View plain)
    {
        auto sealed = cipher_.Encrypt(hash_, plain);
        MixHash(sealed);
        return sealed;
    }

    // The hash before the ciphertext is mixed in is the associated data.
    Bytes DecryptAndHash(View sealed)
    {
        auto plain = cipher_.Decrypt(hash_, sealed);
        MixHash(sealed);
        return plain;
    }

    Owner<EVP_PKEY> local_;
    Owner<EVP_PKEY> ephemeral_;
    Bytes32 hash_{};
    Bytes32 chaining_{};
    Cipher cipher_;
};

// A snapshot request waiting for the peer's offer.
struct Requested
{
};

// The one file transfer of an accepted snapshot offer.
struct Incoming
{
    std::uint64_t id = 0;
    std::int64_t total = 0;
    std::int64_t received = 0;
    // What the host reported as stored.
    std::int64_t acknowledged = 0;
    // What the last PROGRESS message said.
    std::int64_t progressed = 0;
    Hasher hash;
    // FILE_END arrived and its SHA-256 matched.
    bool verified = false;
};

bool Flag(nlohmann::json const& body, char const* key)
{
    auto member = body.find(key);
    return member != body.end() && member->is_boolean() && member->get<bool>();
}

std::int64_t Count(nlohmann::json const& body, char const* key)
{
    return std::max<std::int64_t>(0, ReadInteger(body, key).value_or(0));
}

// The peer's explanation, cut short and reduced to printable ASCII because it
// ends up in logs.
std::string Reason(nlohmann::json const& body)
{
    std::string text;
    auto member = body.find("reason");
    if (member == body.end() || !member->is_string())
    {
        return text;
    }
    for (unsigned char c : member->get_ref<std::string const&>())
    {
        if (text.size() == maxReason)
        {
            break;
        }
        text.push_back(c >= 0x20 && c < 0x7f ? static_cast<char>(c) : '?');
    }
    return text;
}

std::optional<nlohmann::json> ParseObject(View text, std::size_t limit)
{
    if (text.size() > limit)
    {
        return std::nullopt;
    }
    auto body = ParseJson({reinterpret_cast<char const*>(text.data()), text.size()});
    if (!body || !body->is_object())
    {
        return std::nullopt;
    }
    return body;
}
}

struct Session::State
{
    // Present until the handshake ends.
    std::unique_ptr<Handshake> handshake = std::make_unique<Handshake>();
    bool opened = false;
    Cipher outbound;
    Cipher inbound;
    // Received bytes that are not yet a whole block.
    Bytes pending;
    std::uint64_t session = 0;
    std::variant<std::monostate, Requested, Incoming> snapshot;

    std::vector<char> Open()
    {
        if (opened)
        {
            throw std::logic_error("Open was called twice");
        }
        opened = true;
        std::vector<char> output;
        AppendBlock(output, handshake->Start());
        return output;
    }

    std::vector<Event> Receive(std::span<char const> input, std::vector<char>& output)
    {
        if (!opened)
        {
            throw std::logic_error("Open was not called");
        }
        auto first = reinterpret_cast<std::uint8_t const*>(input.data());
        pending.insert(pending.end(), first, first + input.size());
        std::vector<Event> events;
        std::size_t consumed = 0;
        for (;;)
        {
            View rest(pending.data() + consumed, pending.size() - consumed);
            if (rest.size() < 4)
            {
                break;
            }
            auto length = Reader(rest).Number(4);
            if (length > (handshake ? maxHandshake : maxBlock))
            {
                Throw("block too large");
            }
            if (rest.size() - 4 < length)
            {
                break;
            }
            auto body = rest.subspan(4, length);
            consumed += 4 + length;
            if (handshake)
            {
                Establish(body, output, events);
            }
            else
            {
                Dispatch(inbound.Decrypt(View{}, body), output, events);
            }
        }
        pending.erase(pending.begin(), pending.begin() + static_cast<std::ptrdiff_t>(consumed));
        return events;
    }

    std::vector<char> RequestBatch(int limit)
    {
        Require();
        std::vector<char> output;
        SendJson("randomTorrents", nlohmann::json{{"limit", std::clamp(limit, 1, maxBatch)}}, output);
        return output;
    }

    std::vector<char> RequestSnapshot()
    {
        Require();
        if (!std::holds_alternative<std::monostate>(snapshot))
        {
            throw std::logic_error("a snapshot is already requested");
        }
        ++session;
        snapshot = Requested{};
        std::vector<char> output;
        SendJson("databaseRequest", nlohmann::json{{"session", session}}, output);
        return output;
    }

    std::vector<char> Acknowledge(std::int64_t bytes)
    {
        std::vector<char> output;
        auto incoming = std::get_if<Incoming>(&snapshot);
        if (!incoming)
        {
            return output;
        }
        if (bytes < 0 || bytes > incoming->received)
        {
            throw std::invalid_argument("acknowledged more bytes than were delivered");
        }
        if (bytes <= incoming->acknowledged)
        {
            return output;
        }
        incoming->acknowledged = bytes;
        if (bytes == incoming->total || bytes - incoming->progressed >= progressStep)
        {
            auto payload = Control(progressOp, incoming->id);
            Put(payload, static_cast<std::uint64_t>(bytes), 8);
            Send(fileMessage, payload, output);
            incoming->progressed = bytes;
        }
        Conclude(output);
        return output;
    }

    std::vector<char> Cancel()
    {
        std::vector<char> output;
        if (std::holds_alternative<std::monostate>(snapshot))
        {
            return output;
        }
        SendJson("databaseCancel", nlohmann::json{{"session", session}}, output);
        if (auto incoming = std::get_if<Incoming>(&snapshot))
        {
            Send(fileMessage, Control(cancelOp, incoming->id), output);
        }
        snapshot = std::monostate{};
        return output;
    }

    void Require() const
    {
        if (handshake)
        {
            throw std::logic_error("the handshake is not finished");
        }
    }

    // Seals one message into the next block.
    void Send(std::uint8_t type, View payload, std::vector<char>& output)
    {
        Bytes inner{type, 0, 0, 0};
        Append(inner, payload);
        AppendBlock(output, outbound.Encrypt(View{}, inner));
    }

    void SendJson(std::string_view name, nlohmann::json const& body, std::vector<char>& output)
    {
        Bytes payload;
        Put(payload, name.size(), 2);
        Append(payload, AsBytes(name));
        Append(payload, AsBytes(body.dump()));
        Send(typedMessage, payload, output);
    }

    // A file-transfer payload starts with its opcode and the transfer's id.
    static Bytes Control(std::uint8_t op, std::uint64_t id)
    {
        Bytes payload{op};
        Put(payload, id, 8);
        return payload;
    }

    void Respond(std::uint64_t id, bool accept, std::vector<char>& output)
    {
        auto payload = Control(responseOp, id);
        payload.push_back(accept);
        Send(fileMessage, payload, output);
    }

    void SendComplete(std::uint64_t id, bool ok, std::vector<char>& output)
    {
        auto payload = Control(completeOp, id);
        payload.push_back(ok);
        Send(fileMessage, payload, output);
    }

    void Establish(View body, std::vector<char>& output, std::vector<Event>& events)
    {
        auto secured = handshake->Answer(body);
        AppendBlock(output, secured.reply);
        outbound = secured.send;
        inbound = secured.receive;
        Hasher hasher;
        hasher.Update(secured.remote);
        events.push_back(Identified{Hex(hasher.Finish())});
        handshake.reset();
        SendJson("client_info",
            nlohmann::json{{"clientVersion", "TinyTorrent"}, {"torrents", 0}, {"files", 0}, {"totalSize", 0},
                {"peersConnected", 0}, {"connectedAt", 0}, {"databaseSharing", false}},
            output);
    }

    // Rats servers send identify, peer exchange, storage and feed messages
    // unasked. Anything not listed here is read and ignored.
    void Dispatch(Bytes const& inner, std::vector<char>& output, std::vector<Event>& events)
    {
        if (inner.size() < 4)
        {
            Throw("message too short");
        }
        View payload = View(inner).subspan(4);
        if (inner[0] == typedMessage)
        {
            Typed(payload, events);
        }
        else if (inner[0] == fileMessage)
        {
            Transfer(payload, output, events);
        }
    }

    void Typed(View payload, std::vector<Event>& events)
    {
        Reader reader(payload);
        auto name = reader.Take(reader.Number(2));
        auto text = reader.Rest();
        if (!reader.Ok())
        {
            return;
        }
        std::string_view type(reinterpret_cast<char const*>(name.data()), name.size());
        if (type == "client_info")
        {
            if (auto body = ParseObject(text, maxStatusText))
            {
                events.push_back(ReadInfo(*body));
            }
        }
        else if (type == "randomTorrents_response")
        {
            if (auto body = ParseObject(text, maxBatchText))
            {
                events.push_back(ReadBatch(*body));
            }
        }
        else if (type == "databaseRequest_response")
        {
            if (auto body = ParseObject(text, maxStatusText))
            {
                Response(*body, events);
            }
        }
        else if (type == "databaseProgress")
        {
            if (auto body = ParseObject(text, maxStatusText))
            {
                Progress(*body, events);
            }
        }
    }

    static Info ReadInfo(nlohmann::json const& body)
    {
        Info info;
        info.torrents = body.contains("torrents") ? Count(body, "torrents") : Count(body, "torrentsCount");
        info.sharing = Flag(body, "databaseSharing");
        return info;
    }

    static Batch ReadBatch(nlohmann::json const& body)
    {
        Batch batch;
        auto list = body.find("torrents");
        if (list == body.end() || !list->is_array())
        {
            return batch;
        }
        int examined = 0;
        for (auto const& item : *list)
        {
            if (examined++ == maxBatch)
            {
                break;
            }
            if (auto record = Record::Parse(item))
            {
                batch.records.push_back(std::move(*record));
            }
        }
        return batch;
    }

    // True when the message answers the outstanding request. The "unsupported"
    // refusal carries no session, so a missing one counts.
    bool Answers(nlohmann::json const& body) const
    {
        if (!std::holds_alternative<Requested>(snapshot))
        {
            return false;
        }
        auto answered = ReadInteger(body, "session");
        return !answered || static_cast<std::uint64_t>(*answered) == session;
    }

    void Response(nlohmann::json const& body, std::vector<Event>& events)
    {
        if (!Answers(body))
        {
            return;
        }
        if (!Flag(body, "accepted"))
        {
            Refused refused;
            refused.reason = Reason(body);
            auto wait = ReadInteger(body, "retryAfter");
            if (wait && *wait > 0)
            {
                refused.retryAfter = *wait;
            }
            snapshot = std::monostate{};
            events.push_back(std::move(refused));
        }
        else if (Flag(body, "ready"))
        {
            events.push_back(Prepared{Count(body, "bytes")});
        }
        else
        {
            events.push_back(Preparing{0, Count(body, "torrents")});
        }
    }

    void Progress(nlohmann::json const& body, std::vector<Event>& events)
    {
        if (Answers(body))
        {
            events.push_back(Preparing{Count(body, "processed"), Count(body, "total")});
        }
    }

    Incoming* Active(std::uint64_t id)
    {
        auto incoming = std::get_if<Incoming>(&snapshot);
        return incoming && incoming->id == id ? incoming : nullptr;
    }

    void Transfer(View payload, std::vector<char>& output, std::vector<Event>& events)
    {
        Reader reader(payload);
        switch (reader.Number(1))
        {
        case offerOp:
            Offer(reader, output, events);
            break;
        case chunkOp:
            Chunk(reader, output, events);
            break;
        case endOp:
            End(reader, output, events);
            break;
        case completeOp:
        case cancelOp:
        {
            // The sender gave up. It never sends a successful COMPLETE.
            auto id = reader.Number(8);
            if (reader.Ok() && Active(id))
            {
                snapshot = std::monostate{};
                events.push_back(Failed{"the sender ended the transfer"});
            }
            break;
        }
        default:
            break;
        }
    }

    void Offer(Reader& reader, std::vector<char>& output, std::vector<Event>& events)
    {
        auto id = reader.Number(8);
        auto flags = reader.Number(1);
        auto total = reader.Number(8);
        reader.Take(reader.Number(2));
        auto count = reader.Number(4);
        if (!reader.Ok() || count * 10 > reader.Remaining())
        {
            return;
        }
        std::uint64_t size = 0;
        if (count == 1)
        {
            reader.Take(reader.Number(2));
            size = reader.Number(8);
        }
        if (!reader.Ok() || Active(id))
        {
            return;
        }
        // The Rats snapshot is one file that is not a directory.
        bool valid = flags == 0 && count == 1 && size == total && total > 0 &&
            total <= static_cast<std::uint64_t>(maxSnapshot);
        bool wanted = std::holds_alternative<Requested>(snapshot);
        Respond(id, wanted && valid, output);
        if (!wanted)
        {
            return;
        }
        if (!valid)
        {
            snapshot = std::monostate{};
            events.push_back(Failed{"the offer is not a single snapshot file"});
            return;
        }
        snapshot = Incoming{.id = id, .total = static_cast<std::int64_t>(total)};
        events.push_back(Offered{static_cast<std::int64_t>(total)});
    }

    void Chunk(Reader& reader, std::vector<char>& output, std::vector<Event>& events)
    {
        auto id = reader.Number(8);
        auto index = reader.Number(4);
        auto offset = reader.Number(8);
        auto data = reader.Rest();
        auto incoming = Active(id);
        if (!reader.Ok() || !incoming)
        {
            return;
        }
        auto remaining = static_cast<std::uint64_t>(incoming->total - incoming->received);
        if (incoming->verified || index != 0 || offset != static_cast<std::uint64_t>(incoming->received) ||
            data.size() > remaining)
        {
            Abort("unexpected chunk", output, events);
            return;
        }
        if (data.empty())
        {
            return;
        }
        // The sender stops at its window beyond our last PROGRESS, so an honest
        // one never passes this; the host keeps every unacknowledged byte.
        if (incoming->received + static_cast<std::int64_t>(data.size()) - incoming->acknowledged > maxUnacknowledged)
        {
            Throw("the sender ignored the transfer window");
        }
        incoming->hash.Update(data);
        incoming->received += static_cast<std::int64_t>(data.size());
        Data chunk;
        Append(chunk.bytes, data);
        events.push_back(std::move(chunk));
    }

    void End(Reader& reader, std::vector<char>& output, std::vector<Event>& events)
    {
        auto id = reader.Number(8);
        auto index = reader.Number(4);
        auto expected = reader.Take(32);
        auto incoming = Active(id);
        if (!reader.Ok() || !incoming)
        {
            return;
        }
        if (incoming->verified || index != 0 || incoming->received != incoming->total)
        {
            Abort("unexpected end of file", output, events);
            return;
        }
        auto actual = incoming->hash.Finish();
        if (!std::ranges::equal(actual, expected))
        {
            Abort("the file's SHA-256 does not match", output, events);
            return;
        }
        incoming->verified = true;
        events.push_back(Finished{});
        Conclude(output);
    }

    // Ends the transfer and tells the sender that it failed.
    void Abort(char const* reason, std::vector<char>& output, std::vector<Event>& events)
    {
        if (auto incoming = std::get_if<Incoming>(&snapshot))
        {
            SendComplete(incoming->id, false, output);
        }
        snapshot = std::monostate{};
        events.push_back(Failed{reason});
    }

    // Sends COMPLETE once the file is verified and the host has stored all of it.
    void Conclude(std::vector<char>& output)
    {
        auto incoming = std::get_if<Incoming>(&snapshot);
        if (incoming && incoming->verified && incoming->acknowledged == incoming->total)
        {
            SendComplete(incoming->id, true, output);
            snapshot = std::monostate{};
        }
    }
};

Session::Session() : state_(std::make_unique<State>()) {}

Session::~Session() = default;

std::vector<char> Session::Open()
{
    return state_->Open();
}

std::vector<Event> Session::Receive(std::span<char const> input, std::vector<char>& output)
{
    return state_->Receive(input, output);
}

bool Session::Ready() const
{
    return !state_->handshake;
}

std::vector<char> Session::RequestBatch(int limit)
{
    return state_->RequestBatch(limit);
}

std::vector<char> Session::RequestSnapshot()
{
    return state_->RequestSnapshot();
}

std::vector<char> Session::Acknowledge(std::int64_t bytes)
{
    return state_->Acknowledge(bytes);
}

std::vector<char> Session::Cancel()
{
    return state_->Cancel();
}
}
