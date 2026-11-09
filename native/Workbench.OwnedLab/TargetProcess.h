#pragma once
#include "Protocol.h"
namespace owned {
struct TargetProcess {
    Handle process, thread, ready;
    std::wstring imagePath; Hash imageHash; Identity identity{}; HWND window = nullptr;
    explicit TargetProcess(std::string_view, const Hash&);
    void Verify() const;
    int CloseAndJoin(DWORD);
};
}
