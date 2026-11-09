#include "../../native/Workbench.OwnedLab/Main.h"
#include "../../native/Workbench.OwnedLab/Protocol.h"
namespace owned { int RunBroker() { const auto packet=ReadPacket(GetStdHandle(STD_INPUT_HANDLE)); if (packet.kind!=Init) return 3; Sleep(INFINITE); return 5; } }
