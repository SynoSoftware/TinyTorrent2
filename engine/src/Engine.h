#pragma once

#include <nlohmann/json.hpp>
#include <filesystem>
#include <functional>
#include <memory>
#include <string>

namespace tiny
{
using Json = nlohmann::json;

class Engine
{
public:
    Engine(std::filesystem::path directory, std::function<void()> wake);
    ~Engine();
    Engine(Engine const&) = delete;
    Engine& operator=(Engine const&) = delete;

    void Execute(Json const& request, std::function<void(Json)> reply);
    void Tick();
    void Disconnect(std::string const& connection);
    void Shutdown(std::function<void(bool)> completion);
    Json Snapshot() const;
    std::string Language() const;
    bool IsStopping() const;
    bool IsLoading() const;
    bool HasStorageFailure() const;
    bool ShowsAdd() const;
    std::string DefaultDestination() const;

private:
    class State;
    std::unique_ptr<State> state_;
};

std::string Utf8(std::wstring const& value);
std::wstring Wide(std::string const& value);
}
