use crate::errors::{hookerr::HookError, DynErr};

fn disable_vmp_hook() {
    use core::ffi::c_void;
    use windows::{Win32::System::{LibraryLoader::{GetModuleHandleA, GetProcAddress}, Memory::{VirtualProtect, PAGE_EXECUTE_READWRITE, PAGE_PROTECTION_FLAGS}}, s};

    unsafe {
        let h = GetModuleHandleA(s!("ntdll")).unwrap();
        let f = GetProcAddress(h, s!("NtProtectVirtualMemory")).unwrap();
        if *(f as *const u8) != 0x4C {
            let mut old = PAGE_PROTECTION_FLAGS(0);
            let _ = VirtualProtect(f as *const c_void, 7, PAGE_EXECUTE_READWRITE, &mut old);
    
            let bytes: [u8; 7] = [0x4C, 0x8B, 0xD1, 0xB8, 0x50, 0x00, 0x00];
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), f as *mut u8, 7);
    
            let _ = VirtualProtect(f as *const c_void, 7, old, &mut old);
        }
    }
}

pub fn hook(target: usize, detour: usize) -> Result<usize, HookError> {
    if target == 0 {
        return Err(HookError::Nullpointer("target".to_string()));
    }

    if detour == 0 {
        return Err(HookError::Nullpointer("detour".to_string()));
    }

    unsafe {
        disable_vmp_hook();

        let trampoline = dobby_rs::hook(target as dobby_rs::Address, detour as dobby_rs::Address)
            .map_err(|e| HookError::Failed(e.to_string()));

        let trampoline = match trampoline {
            Ok(t) => t,
            Err(e) => return Err(e),
        };

        if trampoline.is_null() {
            return Err(HookError::Null);
        }

        //return Ok with type annotations
        Ok(trampoline as usize)
    }
}

pub fn unhook(target: usize) -> Result<(), DynErr> {
    if target == 0 {
        return Err(HookError::Nullpointer("target".to_string()).into());
    }

    unsafe {
        disable_vmp_hook();

        dobby_rs::unhook(target as dobby_rs::Address)?;
    }

    Ok(())
}
