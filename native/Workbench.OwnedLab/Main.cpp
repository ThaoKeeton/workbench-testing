#include "Main.h"
#include "Protocol.h"
#include <shellapi.h>
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    int count = 0; auto argv = CommandLineToArgvW(GetCommandLineW(), &count); if (!argv) return 3;
    int result = 3;
    try {
        if (count == 2 && std::wstring_view(argv[1]) == L"--broker") result = owned::RunBroker();
        else if (count == 3 && std::wstring_view(argv[1]) == L"--target") result = owned::RunTarget(argv[2]);
    } catch (...) { result = 5; }
    LocalFree(argv); return result;
}
