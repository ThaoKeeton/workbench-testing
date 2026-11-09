#pragma once
#include "TargetProcess.h"
#include <ole2.h> // Explicit COM prerequisites: Protocol.h uses lean Windows headers.
#include <UIAutomation.h>
namespace owned {
template<class T> struct Com {
    T* value=nullptr; ~Com() { if (value) value->Release(); }
    Com()=default; Com(const Com&)=delete; Com& operator=(const Com&)=delete;
    T** Put() { return &value; } T* operator->() const { return value; }
};
struct Automation {
    Com<IUIAutomation> automation;
    Automation(); ~Automation();
    State Read(const TargetProcess&); void Set(const TargetProcess&,std::wstring_view); void Save(const TargetProcess&);
    void Element(const TargetProcess&,int,IUIAutomationElement**);
};
}
