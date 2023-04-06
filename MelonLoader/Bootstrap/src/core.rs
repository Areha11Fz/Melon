use windows::{Win32::{System::{Threading::{OpenEventA, SetEvent, EVENT_ALL_ACCESS, CreateThread, THREAD_CREATION_FLAGS}, LibraryLoader::GetModuleHandleExA, SystemServices::DLL_PROCESS_ATTACH}, Foundation::{CloseHandle, WPARAM, LPARAM, HINSTANCE}}, s};

use crate::{console, errors::DynErr, hooks, internal_failure, logging::logger};

#[no_mangle]
pub fn CBTProc(_: u32, _: WPARAM, _: LPARAM) -> u64 {
    return 0
}

#[no_mangle]
pub fn DllMain(_: HINSTANCE, call_reason: u32, _: u64) -> bool {
    match call_reason {
        DLL_PROCESS_ATTACH => init_filter(),
        _ => true
    }
}

fn startup() {
    init().unwrap_or_else(|e| {
        internal_failure!("Failed to initialize MelonLoader: {}", e.to_string());
    })
}

fn init_filter() -> bool {
    if let Ok(path) = std::env::current_exe() {
        if path.file_name().unwrap_or_default() != "GenshinImpact.exe" {
            return false;
        }

        unsafe {
            match OpenEventA(EVENT_ALL_ACCESS, false, s!("MelonLauncher_Event")) {
                Ok(event) => {
                    let _ = SetEvent(event);
                    let _ = CloseHandle(event);
                }
                Err(_) => (),
            }

            // bump ref count
            let mut m : HINSTANCE = Default::default();
            let _ = GetModuleHandleExA(0, s!("Bootstrap.dll"), &mut m);

            let _ = CreateThread(None, 0, Some(std::mem::transmute(startup as fn())), None, THREAD_CREATION_FLAGS(0), None);
        }
        return true;
    }
    return false;
}

fn init() -> Result<(), DynErr> {
    console::init()?;
    logger::init()?;

    hooks::init_hook::hook()?;

    console::null_handles()?;

    Ok(())
}

pub fn shutdown() {
    std::process::exit(0);
}
