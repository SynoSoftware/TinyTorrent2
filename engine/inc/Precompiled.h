#pragma once

// Engine.vcxproj includes this first in every engine source file. It holds the
// large third-party headers, which change only with 3rdParty, so the compiler
// and IntelliSense parse them once for the project instead of once per file.
#include <libtorrent/add_torrent_params.hpp>
#include <libtorrent/alert_types.hpp>
#include <libtorrent/session.hpp>
#include <libtorrent/torrent_handle.hpp>
#include <libtorrent/torrent_info.hpp>
#include <libtorrent/torrent_status.hpp>
#include <nlohmann/json.hpp>
