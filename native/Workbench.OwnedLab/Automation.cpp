#include "Automation.h"
namespace owned {
static void Ok(HRESULT hr) { if (FAILED(hr)) Fail(5); }
struct String { BSTR value=nullptr; ~String() { SysFreeString(value); } std::wstring Copy() const { return value ? std::wstring(value,SysStringLen(value)) : L""; } };
Automation::Automation() { Ok(CoInitializeEx(nullptr,COINIT_MULTITHREADED)); try { Ok(CoCreateInstance(__uuidof(CUIAutomation),nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(automation.Put()))); } catch (...) { CoUninitialize(); throw; } }
Automation::~Automation() { if (automation.value) { automation.value->Release(); automation.value=nullptr; } CoUninitialize(); }
void Automation::Element(const TargetProcess& target,int id,IUIAutomationElement** out) {
    target.Verify(); const HWND control=GetDlgItem(target.window,id); if (!control) Fail(4);
    Ok(automation->ElementFromHandle(control,out)); int pid=0; Ok((*out)->get_CurrentProcessId(&pid)); if (static_cast<DWORD>(pid) != target.identity.targetPid) Fail(4);
    UIA_HWND native=nullptr; Ok((*out)->get_CurrentNativeWindowHandle(&native)); if (native != control) Fail(4);
}
State Automation::Read(const TargetProcess& target) {
    State state;
    { Com<IUIAutomationElement> element; Element(target,1001,element.Put()); Com<IUIAutomationValuePattern> pattern; Ok(element->GetCurrentPatternAs(UIA_ValuePatternId,IID_PPV_ARGS(pattern.Put()))); String value; Ok(pattern->get_CurrentValue(&value.value)); state.title=value.Copy(); }
    std::wstring* values[]{&state.toast,&state.validation,&state.count};
    for (int i=0;i<3;++i) { Com<IUIAutomationElement> element; Element(target,1003+i,element.Put()); String value; Ok(element->get_CurrentName(&value.value)); *values[i]=value.Copy(); }
    ValidateState(state); target.Verify(); return state;
}
void Automation::Set(const TargetProcess& target,std::wstring_view title) {
    ValidateTitle(title); Com<IUIAutomationElement> element; Element(target,1001,element.Put()); Com<IUIAutomationValuePattern> pattern;
    Ok(element->GetCurrentPatternAs(UIA_ValuePatternId,IID_PPV_ARGS(pattern.Put())));
    String value;
    value.value=SysAllocStringLen(title.empty() ? L"" : title.data(),static_cast<UINT>(title.size()));
    if (!value.value) Fail(5);
    Ok(pattern->SetValue(value.value));
}
void Automation::Save(const TargetProcess& target) { Com<IUIAutomationElement> element; Element(target,1002,element.Put()); Com<IUIAutomationInvokePattern> pattern; Ok(element->GetCurrentPatternAs(UIA_InvokePatternId,IID_PPV_ARGS(pattern.Put()))); Ok(pattern->Invoke()); }
}
