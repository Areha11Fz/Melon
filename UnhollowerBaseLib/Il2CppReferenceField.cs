﻿using System;
using UnhollowerBaseLib;

namespace UnhollowerRuntimeLib
{
    public unsafe class Il2CppReferenceField<TRefObj> where TRefObj : Il2CppObjectBase
    {
        internal Il2CppReferenceField(Il2CppObjectBase obj, string fieldName)
        {
            _obj = obj;
            _fieldPtr = IL2CPP.GetIl2CppField(obj.ObjectClass, fieldName);
        }

        public TRefObj Get()
        {
            IntPtr ptr = *GetPointerToData();
            if (ptr == IntPtr.Zero) return null;
            return (TRefObj)Activator.CreateInstance(typeof(TRefObj), ptr);
        }

        public void Set(TRefObj value) => *GetPointerToData() = value.Pointer;

        public static implicit operator TRefObj(Il2CppReferenceField<TRefObj> _this) => _this.Get();
        public static implicit operator Il2CppReferenceField<TRefObj>(TRefObj _) => throw null;

        private IntPtr* GetPointerToData() => (IntPtr*)(IL2CPP.Il2CppObjectBaseToPtrNotNull(_obj) + (int)IL2CPP.il2cpp_field_get_offset(_fieldPtr));

        private readonly Il2CppObjectBase _obj;
        private readonly IntPtr _fieldPtr;
    }
}
