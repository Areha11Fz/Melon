//! dynamic dobby wrapper

use std::{
    error, mem::transmute
};

use thiserror::Error;

/// dobby errors
#[derive(Debug, Error)]
pub enum DobbyError {
    /// failed to load dobby
    #[error("Failed to load Dobby!")]
    FailedToLoadDobby,

    /// failed to get dobby path
    #[error("Failed to get Dobby path!")]
    FailedToGetDobbyPath,

    /// failed to get dobby name
    #[error("Failed to get Function!")]
    FailedToGetFunction,

    /// failed to hook function
    #[error("Failed to hook Function!")]
    FailedToHookFunction,

    /// failed to unhook function
    #[error("Failed to unhook Function!")]
    FailedToUnhookFunction,
}

/// hook a function pointer
pub fn hook(target: usize, replacement: usize) -> Result<&'static (), Box<dyn error::Error>> {
    use dobby_rs::{Address};

    unsafe {
        use winapi::um::libloaderapi::GetModuleHandleA;
        use winapi::um::libloaderapi::GetProcAddress;
        use winapi::um::memoryapi::VirtualProtect;

        let h = GetModuleHandleA("ntdll\0" as *const _ as *const i8);
        let f = GetProcAddress(h, "NtProtectVirtualMemory\0" as *const _ as *const i8);
        if *(f.cast::<u8>()) != 0x4C {
            let mut old = 0u32;
            let _b = VirtualProtect(f.cast(), 100, 0x40, &mut old as *mut u32);
            let bytes: [u8; 7] = [0x4C, 0x8B, 0xD1, 0xB8, 0x50, 0x00, 0x00];
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), f.cast(), 7);
            let _b = VirtualProtect(f.cast(), 100, old, &mut old as *mut u32);
        }

        let res = dobby_rs::hook(target as Address, replacement as Address)?;
        Ok(transmute(res))
    }
}

/// hook a function pointer
pub fn unhook(target: usize) -> Result<(), Box<dyn error::Error>> {
    use dobby_rs::Address;

    unsafe {
        dobby_rs::unhook(target as Address)?;
    }

    Ok(())
}
