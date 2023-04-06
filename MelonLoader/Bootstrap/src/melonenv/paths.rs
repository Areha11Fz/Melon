use std::path::PathBuf;

use lazy_static::lazy_static;
use unity_rs::runtime::RuntimeType;
use windows::Win32::Foundation::MAX_PATH;

use crate::{errors::DynErr, internal_failure, runtime, constants::W};

use super::args::ARGS;

lazy_static! {
    pub static ref BASE_DIR: W<PathBuf> = {
        match ARGS.base_dir {
            Some(ref path) => W(PathBuf::from(path.clone())),
            None => W(install_dir().unwrap_or_else(|e| {
                internal_failure!("Failed to get base directory: {}", e.to_string());
            })),
        }
    };
    pub static ref GAME_DIR: W<PathBuf> = {
        W(std::env::current_dir().unwrap_or_else(|e| {
            internal_failure!("Failed to get game directory: {}", e.to_string());
        }))
    };
    pub static ref MELONLOADER_FOLDER: W<PathBuf> = W(BASE_DIR.join("MelonLoader"));
    pub static ref DEPENDENCIES_FOLDER: W<PathBuf> = W(MELONLOADER_FOLDER.join("Dependencies"));
    pub static ref SUPPORT_MODULES_FOLDER: W<PathBuf> = W(DEPENDENCIES_FOLDER.join("SupportModules"));
    pub static ref PRELOAD_DLL: W<PathBuf> = W(SUPPORT_MODULES_FOLDER.join("Preload.dll"));
}

pub fn install_dir() -> Result<PathBuf, DynErr> {
    use windows::core::PCSTR;
    use windows::Win32::{System::LibraryLoader::{GetModuleHandleExA, GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, GetModuleFileNameA}, Foundation::HINSTANCE};

    let mut m = HINSTANCE(0);
    let r = unsafe { GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, 
        PCSTR::from_raw(&install_dir as *const _ as *const u8), &mut m) };
    if r == false {
        Err("Failed to get installation directory!")?
    }

    let mut name : [u8; MAX_PATH as usize] = [0; MAX_PATH as usize];
    let len = unsafe { GetModuleFileNameA(m, &mut name) } as usize;
    if len != 0 {
        // INSTALLDIR/MelonLoader/Dependencies/Bootstrap.dll
        let mut path : PathBuf = String::from_utf8(name[0..len].to_vec())?.into();
        let _ = path.pop();
        let _ = path.pop();
        let _ = path.pop();
        return Ok(path)
    }
    else {
        Err("Failed to get installation directory!")?
    }
}

pub fn runtime_dir() -> Result<PathBuf, DynErr> {
    let runtime = runtime!()?;

    let mut path = MELONLOADER_FOLDER.clone();

    match runtime.get_type() {
        RuntimeType::Mono(_) => path.push("net35"),
        RuntimeType::Il2Cpp(_) => path.push("net6"),
    }

    Ok(path.to_path_buf())
}

pub fn get_managed_dir() -> Result<PathBuf, DynErr> {
    let file_path = std::env::current_exe()?;

    let file_name = file_path
        .file_stem()
        .ok_or_else(|| "Failed to get File Stem!")?
        .to_str()
        .ok_or_else(|| "Failed to get File Stem!")?;

    let base_folder = file_path.parent().ok_or_else(|| "Data Path not found!")?;

    let managed_path = base_folder
        .join(format!("{}_Data", file_name))
        .join("Managed");

    match managed_path.exists() {
        true => Ok(managed_path),
        false => {
            let managed_path = base_folder.join("MelonLoader").join("Managed");

            match managed_path.exists() {
                true => Ok(managed_path),
                false => Err("Failed to find the managed directory!")?,
            }
        }
    }
}
