// Entry point: argument parsing, single-instance guard, console attach. Wiring lives in engine_app.cpp.
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <cstdio>
#include <string_view>

#include "engine_app.hpp"
#include "win32/win_common.hpp"

namespace {

void attach_console() {
    if (!AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();
    FILE* f = nullptr;
    freopen_s(&f, "CONOUT$", "w", stdout);
    freopen_s(&f, "CONOUT$", "w", stderr);
}

}  // namespace

int main(int argc, char** argv) {
    mb::host::EngineOptions opts;
    for (int i = 1; i < argc; ++i) {
        const std::string_view a = argv[i];
        if (a == "--console") opts.console = true;
        if (a == "--verbose") opts.verbose = true;
    }
    if (opts.console) attach_console();

    mb::win32::UniqueHandle instance{
        CreateMutexW(nullptr, FALSE, L"Local\\MagneticBorders.Instance")};
    if (!instance || GetLastError() == ERROR_ALREADY_EXISTS) {
        const wchar_t* text = L"Magnetic Borders is already running.";
        MessageBoxW(nullptr, text, L"Magnetic Borders", MB_OK | MB_ICONINFORMATION);
        return 1;
    }
    return mb::host::run_engine(opts);
}
