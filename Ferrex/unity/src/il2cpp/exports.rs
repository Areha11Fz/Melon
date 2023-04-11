//! TODO

use std::ffi::c_char;

use libc::c_void;

use crate::{libs::{LibError, NativeLibrary, NativeMethod}};

use super::types::{Il2CppDomain, Il2CppMethod, Il2CppObject, Il2CppString, Il2CppThread};

/// Various methods exported by il2cpp
///
/// These are direct function pointers, and as such can be hooked
#[derive(Debug, Clone)]
pub struct Il2CppExports {
    /// initializes an il2cpp domain, this is called by unity itself
    pub il2cpp_init: Option<NativeMethod<fn(*const c_char) -> *mut Il2CppDomain>>,
    /// returns the current thread
    pub il2cpp_thread_current: Option<NativeMethod<fn() -> *mut Il2CppThread>>,
    pub il2cpp_runtime_invoke: Option<
        NativeMethod<
            fn(
                *mut Il2CppMethod,
                *mut Il2CppObject,
                *mut *mut c_void,
                *mut *mut Il2CppObject,
            ) -> *mut Il2CppObject,
        >,
    >,
    pub il2cpp_method_get_name: Option<NativeMethod<fn(*mut Il2CppMethod) -> *const c_char>>,
    pub il2cpp_thread_attach: Option<NativeMethod<fn(*mut Il2CppDomain) -> *mut Il2CppThread>>,
    pub il2cpp_domain_get: Option<NativeMethod<fn() -> *mut Il2CppDomain>>,
    pub il2cpp_add_internal_call: Option<NativeMethod<fn(*const c_char, *mut c_void)>>,
    pub il2cpp_string_new: Option<NativeMethod<fn(*const c_char) -> *mut Il2CppString>>,
}

impl Il2CppExports {
    /// looks up and returns all methods from il2cpp
    pub fn new(lib: &NativeLibrary) -> Result<Il2CppExports, LibError> {
        Ok(Il2CppExports {
            il2cpp_init: Some(lib.offset(0x88CC40)?),
            il2cpp_thread_current: None,
            il2cpp_runtime_invoke: Some(lib.offset(0x890770)?),
            il2cpp_method_get_name: Some(lib.offset(0x886AC0)?),
            il2cpp_thread_attach: None,
            il2cpp_domain_get: None,
            il2cpp_add_internal_call: None,
            il2cpp_string_new: None
        })
    }
}
