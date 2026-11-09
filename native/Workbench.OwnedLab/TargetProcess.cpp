#include "TargetProcess.h"

namespace owned {
TargetProcess::TargetProcess(std::string_view session, const Hash& expected) : imagePath(ModulePath()), imageHash(expected) {
    if (ImageHash(imagePath) != expected) Fail(4);
    SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE}; HANDLE rx{}, tx{};
    if (!CreatePipe(&rx,&tx,&security,0)) Fail(5); ready=Handle(rx); Handle writer(tx);
    if (!SetHandleInformation(ready.value,HANDLE_FLAG_INHERIT,0)) Fail(5);
    Handle input(CreateFileW(L"NUL",GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,&security,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr));
    Handle error(CreateFileW(L"NUL",GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,&security,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr)); if (!input || !error) Fail(5);
    SIZE_T size=0; InitializeProcThreadAttributeList(nullptr,1,0,&size); std::vector<std::uint8_t> storage(size);
    auto attributes=reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data()); if (!InitializeProcThreadAttributeList(attributes,1,0,&size)) Fail(5);
    HANDLE inherited[]{input.value,writer.value,error.value};
    if (!UpdateProcThreadAttribute(attributes,0,PROC_THREAD_ATTRIBUTE_HANDLE_LIST,inherited,sizeof(inherited),nullptr,nullptr)) { DeleteProcThreadAttributeList(attributes); Fail(5); }
    STARTUPINFOEXW startup{}; startup.StartupInfo.cb=sizeof(startup); startup.StartupInfo.dwFlags=STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput=input.value; startup.StartupInfo.hStdOutput=writer.value; startup.StartupInfo.hStdError=error.value; startup.lpAttributeList=attributes;
    std::wstring command=L"\""+imagePath+L"\" --target "+std::wstring(session.begin(),session.end());
    wchar_t directory[MAX_PATH+1]{}; const auto n=GetWindowsDirectoryW(directory,MAX_PATH); if (!n || n>=MAX_PATH) { DeleteProcThreadAttributeList(attributes); Fail(5); }
    const std::wstring env=L"SystemRoot="+std::wstring(directory)+L'\0'+L"WINDIR="+std::wstring(directory)+L'\0'+L'\0';
    PROCESS_INFORMATION info{}; const auto ok=CreateProcessW(imagePath.c_str(),command.data(),nullptr,nullptr,TRUE,EXTENDED_STARTUPINFO_PRESENT|CREATE_UNICODE_ENVIRONMENT,const_cast<wchar_t*>(env.data()),directory,&startup.StartupInfo,&info);
    DeleteProcThreadAttributeList(attributes); if (!ok) Fail(5);
    process=Handle(info.hProcess); thread=Handle(info.hThread);
    // Close this parent's copies so target Ready closure/EOF are observable.
    writer=Handle(); input=Handle(); error=Handle();
    const auto packet=ReadPacket(ready.value,true); Reader reader{packet.body};
    const DWORD pid=reader.U32(); const auto birth=reader.U64(), hwnd=reader.U64(); reader.End();
    std::uint8_t trailing=0; DWORD extra=0; const auto eof=ReadFile(ready.value,&trailing,1,&extra,nullptr);
    if (extra != 0 || (!eof && GetLastError() != ERROR_BROKEN_PIPE)) Fail();
    if (packet.session != session || pid != info.dwProcessId || birth != Birth(process.value) || !hwnd) Fail(4);
    identity={GetCurrentProcessId(),pid,Birth(GetCurrentProcess()),birth,hwnd,expected}; window=reinterpret_cast<HWND>(static_cast<std::uintptr_t>(hwnd)); Verify();
    ready=Handle();
}
void TargetProcess::Verify() const {
    VerifyProcess(GetCurrentProcess(),identity.brokerPid,identity.brokerBirth,imagePath,imageHash); VerifyProcess(process.value,identity.targetPid,identity.targetBirth,imagePath,imageHash);
    DWORD pid=0; wchar_t name[64]{}; RECT client{};
    if (!IsWindow(window) || !GetWindowThreadProcessId(window,&pid) || pid != identity.targetPid || !GetClassNameW(window,name,64) || std::wstring_view(name) != WindowClass || !GetClientRect(window,&client) || client.left != 0 || client.top != 0 || client.right != 320 || client.bottom != 180) Fail(4);
    for (const int id : {1001,1002,1003,1004,1005}) { const HWND control=GetDlgItem(window,id); DWORD childPid=0; RECT bounds{};
        if (!control || !GetWindowThreadProcessId(control,&childPid) || childPid != identity.targetPid || GetParent(control) != window || GetDlgCtrlID(control) != id || !GetWindowRect(control,&bounds)) Fail(4);
        MapWindowPoints(nullptr,window,reinterpret_cast<POINT*>(&bounds),2); if (bounds.top < 0 || bounds.bottom > 144) Fail(4);
    }
}
int TargetProcess::CloseAndJoin(DWORD milliseconds) { Verify(); if (!PostMessageW(window,WM_CLOSE,0,0) || WaitForSingleObject(process.value,milliseconds) != WAIT_OBJECT_0) Fail(4); DWORD exit=0; if (!GetExitCodeProcess(process.value,&exit) || exit != 0) Fail(4); return 0; }
}
