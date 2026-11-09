#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <array>
#include <cstdint>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace owned {
inline constexpr wchar_t WindowClass[] = L"PWB.OwnedLab.TaskForm.v1";
inline constexpr char TargetId[] = "owned.win32.task-form.v1";
inline constexpr std::uint32_t MaxBody = 262144, BmpLength = 172854;
enum Kind : std::uint16_t { Init = 1, SetTitle = 2, ClickSave = 3, Observe = 4, Close = 5, TargetReady = 32768, Ready = 32769, Result = 32770, Closed = 32773, Error = 65535 };
struct Failure { std::uint16_t code; };
[[noreturn]] inline void Fail(std::uint16_t code = 3) { throw Failure{ code }; }
struct Handle {
    HANDLE value = nullptr;
    Handle() = default; explicit Handle(HANDLE v) : value(v) { }
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete; Handle& operator=(const Handle&) = delete;
    Handle(Handle&& other) noexcept : value(other.value) { other.value = nullptr; }
    Handle& operator=(Handle&& other) noexcept { if (this != &other) { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); value = other.value; other.value = nullptr; } return *this; }
    explicit operator bool() const { return value && value != INVALID_HANDLE_VALUE; }
};
using Hash = std::array<std::uint8_t, 32>;
struct Packet { Kind kind; std::uint32_t sequence; std::string session; std::vector<std::uint8_t> body; };
struct Writer {
    std::vector<std::uint8_t> bytes;
    void U16(std::uint16_t); void U32(std::uint32_t); void U64(std::uint64_t);
    void Raw(std::span<const std::uint8_t>); void Text(std::wstring_view);
};
struct Reader {
    std::span<const std::uint8_t> bytes; std::size_t position = 0;
    std::span<const std::uint8_t> Take(std::size_t); std::uint16_t U16(); std::uint32_t U32(); std::uint64_t U64();
    std::wstring Text(std::size_t); Hash Digest(); void End();
};
struct State { std::wstring title, toast, validation, count; };
struct Identity { DWORD brokerPid, targetPid; std::uint64_t brokerBirth, targetBirth, hwnd; Hash image; };
Packet ReadPacket(HANDLE, bool internal = false);
void WritePacket(HANDLE, const Packet&);
void WriteIdentity(Writer&, const Identity&); void WriteState(Writer&, const State&);
void ValidateTitle(std::wstring_view); void ValidateState(const State&);
bool HexSession(std::string_view); bool Blank(std::wstring_view);
std::uint64_t Birth(HANDLE); std::wstring ModulePath(); Hash ImageHash(std::wstring_view);
void VerifyProcess(HANDLE, DWORD, std::uint64_t, std::wstring_view, const Hash&);
}
