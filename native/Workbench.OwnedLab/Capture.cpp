#include "Capture.h"
#include <algorithm>
namespace owned {
std::vector<std::uint8_t> Capture(const TargetProcess& target) {
    target.Verify();
#ifdef OWNED_FIXTURE_GDI_BLOCK
    // AcceptanceFixture-only synthetic capture-stage hang, after real target verification.
    // This exercises disposable-broker retirement; it does not simulate a PrintWindow API deadlock.
    Sleep(INFINITE);
#endif
    HDC dc=CreateCompatibleDC(nullptr); if (!dc) Fail(6);
    BITMAPINFO info{}; info.bmiHeader.biSize=40; info.bmiHeader.biWidth=320; info.bmiHeader.biHeight=180; info.bmiHeader.biPlanes=1; info.bmiHeader.biBitCount=24; info.bmiHeader.biCompression=BI_RGB; info.bmiHeader.biSizeImage=172800;
    void* pixels=nullptr; HBITMAP bitmap=CreateDIBSection(dc,&info,DIB_RGB_COLORS,&pixels,nullptr,0);
    if (!bitmap || !pixels) { if (bitmap) DeleteObject(bitmap); DeleteDC(dc); Fail(6); }
    const auto prior=SelectObject(dc,bitmap); std::fill_n(static_cast<std::uint8_t*>(pixels),172800,0);
    // This synchronous operation stays in the disposable owned broker, never the host/UI thread.
    const BOOL printed=PrintWindow(target.window,dc,PW_CLIENTONLY); GdiFlush();
    Writer bytes; bytes.U16(0x4d42); bytes.U32(BmpLength); bytes.U32(0); bytes.U32(54); bytes.U32(40); bytes.U32(320); bytes.U32(180); bytes.U16(1); bytes.U16(24); bytes.U32(0); bytes.U32(172800); bytes.U32(0); bytes.U32(0); bytes.U32(0); bytes.U32(0);
    bytes.Raw({static_cast<const std::uint8_t*>(pixels),172800}); SelectObject(dc,prior); DeleteObject(bitmap); DeleteDC(dc);
    const auto calibrated=[&](int x,std::array<std::uint8_t,3> value) { const auto offset=54+(179-158)*960+x*3; return std::equal(value.begin(),value.end(),bytes.bytes.begin()+offset); };
    if (!printed || bytes.bytes.size()!=BmpLength || !calibrated(286,{113,57,17}) || !calibrated(302,{31,73,149})) Fail(6);
    target.Verify(); return bytes.bytes;
}
}
