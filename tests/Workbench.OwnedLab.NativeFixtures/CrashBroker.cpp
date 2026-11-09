#include "../../native/Workbench.OwnedLab/Main.h"
#include "../../native/Workbench.OwnedLab/Protocol.h"
namespace owned { int RunBroker() { const auto packet=ReadPacket(GetStdHandle(STD_INPUT_HANDLE)); if (packet.kind!=Init) return 3; RaiseFailFastException(nullptr,nullptr,0); return 5; } }
