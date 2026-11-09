#include "Broker.h"
#include "Automation.h"
#include "Capture.h"
#include <memory>
#include <algorithm>
namespace owned {
static std::uint64_t UnixMilliseconds() { FILETIME time{}; GetSystemTimeAsFileTime(&time); const auto ticks=(static_cast<std::uint64_t>(time.dwHighDateTime)<<32)|time.dwLowDateTime; return (ticks-116444736000000000ULL)/10000; }
int RunBroker() {
    const HANDLE input=GetStdHandle(STD_INPUT_HANDLE), output=GetStdHandle(STD_OUTPUT_HANDLE);
    Packet request{Init,0,{}, {}}; std::unique_ptr<TargetProcess> target;
    try {
        request=ReadPacket(input); if (request.kind!=Init || request.sequence!=1) Fail(); Reader init{request.body};
        const auto run=init.Take(32); if (!HexSession({reinterpret_cast<const char*>(run.data()),run.size()})) Fail();
        if (init.Text(32).empty() || init.Text(128).empty() || init.Text(64)!=L"owned.win32.task-form.v1") Fail(); init.Digest(); init.Digest(); const auto expected=init.Digest(); const auto expiry=init.U64(); const auto remaining=init.U32(); init.End();
        const auto utc=UnixMilliseconds(); if (remaining<1 || remaining>60000 || expiry<=utc || expiry>static_cast<std::uint64_t>(INT64_MAX) || ImageHash(ModulePath())!=expected) Fail();
        const auto deadline=GetTickCount64()+std::min<std::uint64_t>(remaining,expiry-utc);
        const auto budget=[&]() -> DWORD { const auto now=GetTickCount64(); if (now>=deadline) Fail(4); return static_cast<DWORD>(std::min<std::uint64_t>(10000,deadline-now)); };
        target=std::make_unique<TargetProcess>(request.session,expected); Automation automation; State state;
        const auto readyEnd=GetTickCount64()+budget();
        for (;;) { state=automation.Read(*target); if (state.title.empty() && state.toast.empty() && state.validation.empty() && state.count==L"0") break; if (GetTickCount64()>=readyEnd) Fail(4); Sleep(20); }
        Writer ready; auto readyIdentity=target->identity;
#ifdef OWNED_FIXTURE_STALE_READY
        ++readyIdentity.targetBirth;
#endif
        WriteIdentity(ready,readyIdentity); WriteState(ready,state); WritePacket(output,{Ready,request.sequence,request.session,ready.bytes});
        std::uint32_t sequence=1; const auto session=request.session;
        for (;;) {
            if (sequence==UINT32_MAX) Fail(); request=ReadPacket(input);
            if (request.sequence!=++sequence || request.session!=session || request.kind<SetTitle || request.kind>Close) Fail(); budget(); Reader body{request.body};
            std::wstring title; if (request.kind==SetTitle) { title=body.Text(1024); ValidateTitle(title); } body.End(); target->Verify();
            if (request.kind==Close) {
                target->CloseAndJoin(budget());
                Writer closed;
#ifdef OWNED_FIXTURE_INVALID_CLOSED_ZERO_EXIT
                closed.U32(1); // Invalid wire receipt; the actual target and broker still exit zero.
#else
                closed.U32(0);
#endif
                WritePacket(output,{Closed,request.sequence,session,closed.bytes}); return 0;
            }
            if (request.kind==SetTitle) automation.Set(*target,title); else if (request.kind==ClickSave) automation.Save(*target);
            // Invoke can enqueue target work: bounded raw UIA polling observes its actual state.
            if (request.kind==ClickSave) { const auto end=GetTickCount64()+budget(); for (;;) { state=automation.Read(*target); if ((!Blank(state.title) && state.toast==L"Saved") || (Blank(state.title) && state.validation==L"Title required")) break; if (GetTickCount64()>=end) Fail(5); Sleep(10); } }
            state=automation.Read(*target); target->Verify(); const auto bmp=Capture(*target); budget();
            Writer result; result.U16(request.kind); auto identity=target->identity;
#ifdef OWNED_FIXTURE_CHANGED_IDENTITY
            ++identity.targetBirth;
#endif
            WriteIdentity(result,identity); WriteState(result,state); result.U32(BmpLength); result.Raw(bmp);
#ifdef OWNED_FIXTURE_BAD_FRAME
            result.bytes[0]=99;
#endif
            WritePacket(output,{Result,request.sequence,session,result.bytes});
        }
    } catch (const Failure& error) {
        if (request.sequence && HexSession(request.session) && request.kind>=Init && request.kind<=Close) { try { Writer body; body.U16(request.kind); body.U16(error.code); WritePacket(output,{Error,request.sequence,request.session,body.bytes}); } catch (...) { } } return error.code;
    } catch (...) { return 5; }
}
}
