#include "Target.h"
#include "Protocol.h"

namespace owned {
static int created = 0;
static std::wstring Title(HWND window) { const HWND edit = GetDlgItem(window, 1001); const auto count = GetWindowTextLengthW(edit); if (count > 256) Fail(); std::wstring value(static_cast<std::size_t>(count) + 1, L'\0'); const auto read = GetWindowTextW(edit, value.data(), count + 1); value.resize(static_cast<std::size_t>(read)); return value; }
static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wp, LPARAM lp) {
    switch (message) {
    case WM_CREATE: {
        const auto instance = reinterpret_cast<LPCREATESTRUCTW>(lp)->hInstance;
        struct Control { const wchar_t* type; const wchar_t* text; int id, x, y, w, h; DWORD style; };
        const Control controls[] = { {L"EDIT",L"",1001,12,12,296,26,WS_BORDER|ES_AUTOHSCROLL}, {L"BUTTON",L"Save",1002,12,44,80,26,BS_PUSHBUTTON}, {L"STATIC",L"",1003,104,48,204,20,0}, {L"STATIC",L"",1004,12,80,296,20,0}, {L"STATIC",L"0",1005,12,112,296,20,0} };
        for (const auto& c : controls) if (!CreateWindowExW(0, c.type, c.text, WS_CHILD | WS_VISIBLE | c.style, c.x,c.y,c.w,c.h,window,reinterpret_cast<HMENU>(static_cast<INT_PTR>(c.id)),instance,nullptr)) return -1;
        SendDlgItemMessageW(window, 1001, EM_SETLIMITTEXT, 256, 0); return 0;
    }
    case WM_COMMAND:
        if (LOWORD(wp) == 1001 && HIWORD(wp) == EN_CHANGE) { SetDlgItemTextW(window,1003,L""); SetDlgItemTextW(window,1004,L""); }
        else if (LOWORD(wp) == 1002 && HIWORD(wp) == BN_CLICKED) {
            try { const auto title = Title(window); ValidateTitle(title); if (Blank(title)) { SetDlgItemTextW(window,1003,L""); SetDlgItemTextW(window,1004,L"Title required"); } else if (created < 20) { ++created; SetDlgItemTextW(window,1003,L"Saved"); SetDlgItemTextW(window,1004,L""); SetDlgItemTextW(window,1005,std::to_wstring(created).c_str()); } }
            catch (...) { DestroyWindow(window); }
        } return 0;
    case WM_PRINTCLIENT:
        [[fallthrough]];
    case WM_PAINT: {
        PAINTSTRUCT paint{}; HDC dc = message == WM_PAINT ? BeginPaint(window,&paint) : reinterpret_cast<HDC>(wp);
        RECT client{0,0,320,180}; FillRect(dc,&client,GetSysColorBrush(COLOR_WINDOW));
        const RECT a{280,152,292,164}, b{296,152,308,164}; HBRUSH first=CreateSolidBrush(RGB(17,57,113)), second=CreateSolidBrush(RGB(149,73,31));
        FillRect(dc,&a,first); FillRect(dc,&b,second); DeleteObject(first); DeleteObject(second);
        if (message == WM_PAINT) EndPaint(window,&paint); return 0;
    }
#ifdef OWNED_FIXTURE_UIA_BLOCK
    case WM_GETOBJECT: Sleep(INFINITE); return 0;
#endif
    case WM_CLOSE: DestroyWindow(window); return 0;
    case WM_DESTROY: PostQuitMessage(0); return 0;
    default: return DefWindowProcW(window,message,wp,lp);
    }
}
int RunTarget(std::wstring_view sessionWide) {
    std::string session; for (const auto c : sessionWide) { if (c > 127) return 3; session.push_back(static_cast<char>(c)); } if (!HexSession(session)) return 3;
#ifdef OWNED_FIXTURE_DPI_FAILURE
    return 6;
#endif
    if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return 6;
    WNDCLASSEXW wc{sizeof(wc)}; wc.lpfnWndProc=WindowProc; wc.hInstance=GetModuleHandleW(nullptr); wc.lpszClassName=WindowClass; wc.hbrBackground=GetSysColorBrush(COLOR_WINDOW);
    if (!RegisterClassExW(&wc)) return 5;
    const DWORD style=WS_OVERLAPPED|WS_CAPTION|WS_SYSMENU; RECT bounds{0,0,320,180}; if (!AdjustWindowRectExForDpi(&bounds,style,FALSE,0,GetDpiForSystem())) return 6;
    HWND window=CreateWindowExW(0,WindowClass,L"Owned Windows pilot task form",style,CW_USEDEFAULT,CW_USEDEFAULT,bounds.right-bounds.left,bounds.bottom-bounds.top,nullptr,nullptr,wc.hInstance,nullptr);
    if (!window) return 5;
    // Account for the actual monitor chosen by window creation, while retaining a physical client.
    bounds={0,0,320,180};
    if (!AdjustWindowRectExForDpi(&bounds,style,FALSE,0,GetDpiForWindow(window)) || !SetWindowPos(window,nullptr,0,0,bounds.right-bounds.left,bounds.bottom-bounds.top,SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE)) { DestroyWindow(window); return 6; }
    RECT client{}; if (!GetClientRect(window,&client) || client.right != 320 || client.bottom != 180) { DestroyWindow(window); return 6; }
    ShowWindow(window,SW_SHOWNOACTIVATE); UpdateWindow(window);
    Writer body; body.U32(GetCurrentProcessId()); body.U64(Birth(GetCurrentProcess())); body.U64(reinterpret_cast<std::uintptr_t>(window));
    WritePacket(GetStdHandle(STD_OUTPUT_HANDLE),{TargetReady,0,session,body.bytes}); CloseHandle(GetStdHandle(STD_OUTPUT_HANDLE)); SetStdHandle(STD_OUTPUT_HANDLE,INVALID_HANDLE_VALUE);
#ifdef OWNED_FIXTURE_EARLY_EXIT
    DestroyWindow(window); return 7;
#endif
    MSG message{}; int read;
    while ((read=GetMessageW(&message,nullptr,0,0)) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
    return read == 0 ? 0 : 5;
}
}
