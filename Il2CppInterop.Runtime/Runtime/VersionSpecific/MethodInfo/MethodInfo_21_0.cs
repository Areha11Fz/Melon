using System;
using System.Runtime.InteropServices;
namespace Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo
{
    [ApplicableToUnityVersionsSince("5.3.6")]
    public unsafe class NativeMethodInfoStructHandler_21_0 : INativeMethodInfoStructHandler
    {
        public int Size() => sizeof(Il2CppMethodInfo_21_0);
        public INativeMethodInfoStruct CreateNewStruct()
        {
            IntPtr ptr = Marshal.AllocHGlobal(Size());
            Il2CppMethodInfo_21_0* _ = (Il2CppMethodInfo_21_0*)ptr;
            *_ = default;
            return new NativeStructWrapper(ptr);
        }
        public INativeMethodInfoStruct Wrap(Il2CppMethodInfo* ptr)
        {
            if (ptr == null) return null;
            return new NativeStructWrapper((IntPtr)ptr);
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        internal unsafe struct GIExtraMethodInfo
        {
            [FieldOffset(16)]
            public byte* name;
        }

        [StructLayout(LayoutKind.Explicit, Size = 200)]
        internal unsafe struct Il2CppMethodInfo_21_0
        {
            [FieldOffset(0)]
            public Il2CppClass* declaring_type;
            [FieldOffset(8)]
            public void* methodPointer;
            [FieldOffset(16)]
            public void* invoker_method;
            [FieldOffset(0x30)]
            public ushort slot;
            [FieldOffset(0x32)]
            public byte parameters_count;
            [FieldOffset(0x40)]
            public GIExtraMethodInfo* extra_info;

            [FieldOffset(0)]
            public Il2CppTypeStruct* return_type;
            [FieldOffset(0)]
            public Il2CppParameterInfo* parameters;
            [FieldOffset(0)]
            public void* runtime_data;
            [FieldOffset(0)]
            public void* generic_data;
            [FieldOffset(0)]
            public int customAttributeIndex;
            [FieldOffset(0)]
            public uint token;
            [FieldOffset(0)]
            public ushort flags;
            [FieldOffset(0)]
            public ushort iflags;
            [FieldOffset(0)]
            public Bitfield0 _bitfield0;
            internal enum Bitfield0 : byte
            {
                BIT_is_generic = 0,
                is_generic = (1 << BIT_is_generic),
                BIT_is_inflated = 1,
                is_inflated = (1 << BIT_is_inflated),
            }

        }

        internal class NativeStructWrapper : INativeMethodInfoStruct
        {
            public NativeStructWrapper(IntPtr ptr) => Pointer = ptr;
            private static int _bitfield0offset = Marshal.OffsetOf<Il2CppMethodInfo_21_0>(nameof(Il2CppMethodInfo_21_0._bitfield0)).ToInt32();
            public IntPtr Pointer { get; }
            private Il2CppMethodInfo_21_0* _ => (Il2CppMethodInfo_21_0*)Pointer;
            public Il2CppMethodInfo* MethodInfoPointer => (Il2CppMethodInfo*)Pointer;
            public ref IntPtr Name => ref *(IntPtr*)&_->extra_info->name;
            public ref IntPtr Extra => ref *(IntPtr*)&_->extra_info;
            public ref ushort Slot => ref _->slot;
            public ref IntPtr MethodPointer => ref *(IntPtr*)&_->methodPointer;
            public ref Il2CppClass* Class => ref _->declaring_type;
            public ref IntPtr InvokerMethod => ref *(IntPtr*)&_->invoker_method;
            public ref Il2CppTypeStruct* ReturnType => ref _->return_type;
            public ref Il2CppMethodFlags Flags => ref *(Il2CppMethodFlags*)&_->flags;
            public ref byte ParametersCount => ref _->parameters_count;
            public ref Il2CppParameterInfo* Parameters => ref _->parameters;
            public ref uint Token => ref _->token;
            public bool IsGeneric
            {
                get => this.CheckBit(_bitfield0offset, (int)Il2CppMethodInfo_21_0.Bitfield0.BIT_is_generic);
                set => this.SetBit(_bitfield0offset, (int)Il2CppMethodInfo_21_0.Bitfield0.BIT_is_generic, value);
            }
            public bool IsInflated
            {
                get => this.CheckBit(_bitfield0offset, (int)Il2CppMethodInfo_21_0.Bitfield0.BIT_is_inflated);
                set => this.SetBit(_bitfield0offset, (int)Il2CppMethodInfo_21_0.Bitfield0.BIT_is_inflated, value);
            }
            public bool IsMarshalledFromNative
            {
                get => false;
                set { }
            }
        }

    }

}
