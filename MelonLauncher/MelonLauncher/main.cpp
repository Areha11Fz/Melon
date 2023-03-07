#include <Windows.h>
#include <filesystem>
#include <optional>

namespace fs = std::filesystem;

std::optional<fs::path> this_dir()
{
    TCHAR path[MAX_PATH]{};
    HMODULE mod = GetModuleHandle(NULL);
    if (!GetModuleFileName(mod, path, MAX_PATH))
    {
        printf("GetModuleFileName failed (%i)\n", GetLastError());
        return std::nullopt;
    }

    return fs::path(path).remove_filename();
}

int main()
{
    auto current_dir = this_dir();
    if (!current_dir)
    {
        system("pause");
        return 1;
    }

    auto bootstrap_path = current_dir.value() / "MelonLoader/Dependencies/Bootstrap.dll";
    if (!fs::is_regular_file(bootstrap_path))
    {
        printf("Bootstrap.dll not found\n");
        system("pause");
        return 1;
    }

    auto bootstrap = LoadLibraryEx(bootstrap_path.c_str(), NULL, DONT_RESOLVE_DLL_REFERENCES);
    auto proc = GetProcAddress(bootstrap, "CBTProc");

    HANDLE event = CreateEvent(NULL, FALSE, FALSE, L"MelonLauncher_Event");
    auto hhook = SetWindowsHookEx(WH_CBT, (HOOKPROC)proc, bootstrap, 0);

    printf("Waiting for game...\n");
    WaitForSingleObject(event, INFINITE);
    printf("Injected!\nClosing in 5 seconds...");

    UnhookWindowsHookEx(hhook);
    CloseHandle(event);
    Sleep(5000);
    return 0;
}