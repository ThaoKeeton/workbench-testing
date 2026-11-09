#include "Protocol.h"
#include <bcrypt.h>
#include <algorithm>
#include <limits>

namespace owned {
void Writer::U16(std::uint16_t v) { for (int i = 0; i < 2; ++i) bytes.push_back(static_cast<std::uint8_t>(v >> (8 * i))); }
void Writer::U32(std::uint32_t v) { for (int i = 0; i < 4; ++i) bytes.push_back(static_cast<std::uint8_t>(v >> (8 * i))); }
void Writer::U64(std::uint64_t v) { for (int i = 0; i < 8; ++i) bytes.push_back(static_cast<std::uint8_t>(v >> (8 * i))); }
void Writer::Raw(std::span<const std::uint8_t> v) { bytes.insert(bytes.end(), v.begin(), v.end()); }
static std::string Utf8(std::wstring_view text) {
    if (text.empty()) return {};
    int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    if (!length) Fail(); std::string out(static_cast<std::size_t>(length), '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), out.data(), length, nullptr, nullptr) != length) Fail(); return out;
}
void Writer::Text(std::wstring_view v) { const auto text = Utf8(v); U32(static_cast<std::uint32_t>(text.size())); Raw({ reinterpret_cast<const std::uint8_t*>(text.data()), text.size() }); }
std::span<const std::uint8_t> Reader::Take(std::size_t length) { if (length > bytes.size() - position) Fail(); const auto value = bytes.subspan(position, length); position += length; return value; }
std::uint16_t Reader::U16() { const auto v = Take(2); return static_cast<std::uint16_t>(v[0] | (v[1] << 8)); }
std::uint32_t Reader::U32() { const auto v = Take(4); std::uint32_t n = 0; for (int i = 0; i < 4; ++i) n |= static_cast<std::uint32_t>(v[i]) << (8 * i); return n; }
std::uint64_t Reader::U64() { const auto v = Take(8); std::uint64_t n = 0; for (int i = 0; i < 8; ++i) n |= static_cast<std::uint64_t>(v[i]) << (8 * i); return n; }
std::wstring Reader::Text(std::size_t maximum) {
    const auto length = U32(); if (length > maximum) Fail(); const auto value = Take(length); if (value.empty()) return {};
    const auto data = reinterpret_cast<const char*>(value.data());
    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, data, static_cast<int>(length), nullptr, 0); if (!count) Fail();
    std::wstring text(static_cast<std::size_t>(count), L'\0'); if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, data, static_cast<int>(length), text.data(), count) != count) Fail();
    if (text[0] == 0xfeff || text.find(L'\0') != std::wstring::npos) Fail(); return text;
}
Hash Reader::Digest() { Hash v{}; const auto data = Take(v.size()); std::copy(data.begin(), data.end(), v.begin()); return v; }
void Reader::End() { if (position != bytes.size()) Fail(); }
bool HexSession(std::string_view v) { return v.size() == 32 && std::all_of(v.begin(), v.end(), [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); }); }
static bool Known(Kind k, bool internal) { return internal ? k == TargetReady : (k >= Init && k <= Close) || k == Ready || k == Result || k == Closed || k == Error; }
static void ReadAll(HANDLE h, std::span<std::uint8_t> bytes) { while (!bytes.empty()) { DWORD read = 0; if (!ReadFile(h, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr) || !read) Fail(); bytes = bytes.subspan(read); } }
static void WriteAll(HANDLE h, std::span<const std::uint8_t> bytes) { while (!bytes.empty()) { DWORD count = 0; if (!WriteFile(h, bytes.data(), static_cast<DWORD>(bytes.size()), &count, nullptr) || !count) Fail(); bytes = bytes.subspan(count); } }
Packet ReadPacket(HANDLE h, bool internal) {
    std::array<std::uint8_t, 52> header{}; ReadAll(h, header); Reader r{ header };
    const auto magic = r.Take(8); if (!std::equal(magic.begin(), magic.end(), "PWB-LAB1") || r.U16() != 1) Fail();
    const auto kind = static_cast<Kind>(r.U16()); const auto sequence = r.U32(); const auto length = r.U32(); const auto sessionBytes = r.Take(32);
    std::string session(reinterpret_cast<const char*>(sessionBytes.data()), 32);
    if (!Known(kind, internal) || !HexSession(session) || length > MaxBody || (internal ? sequence != 0 : sequence == 0)) Fail();
    // The fixed header is checked before any attacker-sized body allocation.
    Packet packet{ kind, sequence, session, std::vector<std::uint8_t>(length) }; ReadAll(h, packet.body); return packet;
}
void WritePacket(HANDLE h, const Packet& p) {
    if (p.body.size() > MaxBody || !HexSession(p.session)) Fail(); Writer w;
    w.Raw({ reinterpret_cast<const std::uint8_t*>("PWB-LAB1"), 8 }); w.U16(1); w.U16(p.kind); w.U32(p.sequence); w.U32(static_cast<std::uint32_t>(p.body.size())); w.Raw({ reinterpret_cast<const std::uint8_t*>(p.session.data()), 32 });
    WriteAll(h, w.bytes); WriteAll(h, p.body);
}
void ValidateTitle(std::wstring_view v) {
    if (v.size() > 256 || Utf8(v).size() > 1024 || (!v.empty() && v.front() == 0xfeff)) Fail();
    for (const auto c : v) if (c <= 0x1f || (c >= 0x7f && c <= 0x9f) || c == 0x2028 || c == 0x2029) Fail();
}
bool Blank(std::wstring_view v) { return std::all_of(v.begin(), v.end(), [](wchar_t c) { return c == 0x20 || c == 0xa0 || c == 0x1680 || (c >= 0x2000 && c <= 0x200a) || c == 0x202f || c == 0x205f || c == 0x3000; }); }
void ValidateState(const State& s) {
    ValidateTitle(s.title);
    auto text = [](const std::wstring& v, std::size_t max) { if (Utf8(v).size() > max || (!v.empty() && v.front() == 0xfeff) || v.find(L'\0') != std::wstring::npos) Fail(); };
    text(s.toast, 64); text(s.validation, 128); text(s.count, 2);
    if (s.count.empty() || (s.count.size() == 2 && s.count[0] == L'0') || !std::all_of(s.count.begin(), s.count.end(), [](wchar_t c) { return c >= L'0' && c <= L'9'; }) || std::stoi(s.count) > 20) Fail();
}
void WriteIdentity(Writer& w, const Identity& i) { w.U32(i.brokerPid); w.U64(i.brokerBirth); w.U32(i.targetPid); w.U64(i.targetBirth); w.U64(i.hwnd); w.Raw(i.image); }
void WriteState(Writer& w, const State& s) { ValidateState(s); w.Text(s.title); w.Text(s.toast); w.Text(s.validation); w.Text(s.count); }
std::uint64_t Birth(HANDLE process) { FILETIME a{}, b{}, c{}, d{}; if (!GetProcessTimes(process, &a, &b, &c, &d)) Fail(4); return (static_cast<std::uint64_t>(a.dwHighDateTime) << 32) | a.dwLowDateTime; }
std::wstring ModulePath() { std::wstring path(32768, L'\0'); const DWORD n = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size())); if (!n || n == path.size()) Fail(5); path.resize(n); return path; }
Hash ImageHash(std::wstring_view path) {
    Handle file(CreateFileW(std::wstring(path).c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr)); if (!file) Fail(4);
    BCRYPT_ALG_HANDLE algorithm{}; BCRYPT_HASH_HANDLE hash{}; Hash digest{};
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) Fail(5);
    const auto created = BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0);
    if (created < 0) { BCryptCloseAlgorithmProvider(algorithm, 0); Fail(5); }
    bool ok = true; std::array<std::uint8_t, 65536> bytes{}; DWORD read = 0;
    while (true) { if (!ReadFile(file.value, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr)) { ok = false; break; } if (!read) break; if (BCryptHashData(hash, bytes.data(), read, 0) < 0) { ok = false; break; } }
    if (BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) < 0) ok = false;
    BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0); if (!ok) Fail(4); return digest;
}
void VerifyProcess(HANDLE process, DWORD pid, std::uint64_t birth, std::wstring_view path, const Hash& hash) {
    if (GetProcessId(process) != pid || WaitForSingleObject(process, 0) != WAIT_TIMEOUT || Birth(process) != birth) Fail(4);
    std::wstring actual(32768, L'\0'); DWORD size = static_cast<DWORD>(actual.size());
    if (!QueryFullProcessImageNameW(process, 0, actual.data(), &size)) Fail(4); actual.resize(size);
    if (CompareStringOrdinal(actual.data(), static_cast<int>(actual.size()), path.data(), static_cast<int>(path.size()), TRUE) != CSTR_EQUAL || ImageHash(path) != hash) Fail(4);
}
}
