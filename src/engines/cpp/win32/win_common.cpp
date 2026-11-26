// visual_frame implementation (DWM extended frame bounds).
#include <dwmapi.h>

#include "win_common.hpp"

namespace mb::win32 {

std::optional<Rect> visual_frame(HWND hwnd) {
    RECT r{};
    if (FAILED(DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &r, sizeof(r))))
        return std::nullopt;
    return Rect{r.left, r.top, r.right, r.bottom};
}

}  // namespace mb::win32
