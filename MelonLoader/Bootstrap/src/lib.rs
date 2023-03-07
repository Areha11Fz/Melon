#![allow(non_snake_case)]
#![deny(
    missing_debug_implementations,
    warnings,
    clippy::extra_unused_lifetimes,
    clippy::from_over_into,
    clippy::needless_borrow,
    clippy::new_without_default,
    clippy::useless_conversion
)]
#![forbid(rust_2018_idioms)]
#![allow(clippy::inherent_to_string, clippy::type_complexity, improper_ctypes)]
#![cfg_attr(docsrs, feature(doc_cfg))]

use managers::core;
use winapi::{shared::minwindef::{WPARAM, LPARAM, HINSTANCE, DWORD, LPVOID, BOOL, TRUE, FALSE}, um::{winnt::{DLL_PROCESS_ATTACH, EVENT_ALL_ACCESS}, synchapi::{OpenEventA, SetEvent}, handleapi::CloseHandle, processthreadsapi::CreateThread, libloaderapi::GetModuleHandleExA}};

pub mod utils;
pub mod managers;

#[no_mangle]
pub fn CBTProc(_: u32, _: WPARAM, _: LPARAM) -> u64 {
    return 0
}

#[no_mangle]
pub fn DllMain(_: HINSTANCE, call_reason: DWORD, _: LPVOID) -> BOOL {
    match call_reason {
        DLL_PROCESS_ATTACH => if init_filter() == true { TRUE } else { FALSE },
        _ => TRUE
    }
}

fn init() {
    core::init().unwrap_or_else(|e| {
        internal_failure!("Failed to initialize Bootstrap: {}", e);
    });
}

fn init_filter() -> bool {
    if let Ok(path) = std::env::current_exe() {
        let name = path.file_name().unwrap_or_default();
        if name == "GenshinImpact.exe" {
            unsafe {
                let event = OpenEventA(EVENT_ALL_ACCESS, 0, "MelonLauncher_Event\0".as_ptr().cast());
                if event != std::ptr::null_mut() {
                    SetEvent(event);
                    CloseHandle(event);
                }

                // bump ref count
                let mut m = std::ptr::null_mut();
                GetModuleHandleExA(0, "Bootstrap.dll\0".as_ptr().cast(), &mut m); 

                CreateThread(std::ptr::null_mut(), 0, std::mem::transmute(init as fn()), std::ptr::null_mut(), 0, std::ptr::null_mut());
            }
            return true;
        }
    }
    return false;
}
