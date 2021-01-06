using System.Runtime.InteropServices;

namespace IoUring.Transport.Internals
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct sock_filter
    {
        public ushort code;
        public byte jt;
        public byte jf;
        public uint k;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct sock_fprog
    {
        public ushort len;
        public sock_filter *filter;
    }

    internal static class Bpf
    {
        /* Instruction classes */
        public static ushort BPF_CLASS(ushort code) => (ushort) (code & 0x07);
        public const ushort BPF_LD = 0x00;
        public const ushort BPF_LDX = 0x01;
        public const ushort BPF_ST = 0x02;
        public const ushort BPF_STX = 0x03;
        public const ushort BPF_ALU = 0x04;
        public const ushort BPF_JMP = 0x05;
        public const ushort BPF_RET = 0x06;
        public const ushort BPF_MISC = 0x07;

        public static ushort BPF_SIZE(ushort code) => (ushort) (code & 0x18);
        public const ushort BPF_W = 0x00; /* 32-bit */
        public const ushort BPF_H = 0x08; /* 16-bit */
        public const ushort BPF_B = 0x10; /*  8-bit */

        public static ushort BPF_MODE(ushort code) => (ushort) (code & 0xe0);
        public const ushort BPF_IMM = 0x00;
        public const ushort BPF_ABS = 0x20;
        public const ushort BPF_IND = 0x40;
        public const ushort BPF_MEM = 0x60;
        public const ushort BPF_LEN = 0x80;
        public const ushort BPF_MSH = 0xa0;

        public static ushort BPF_OP(ushort code) => (ushort) (code & 0xf0);
        public const ushort BPF_ADD = 0x00;
        public const ushort BPF_SUB = 0x10;
        public const ushort BPF_MUL = 0x20;
        public const ushort BPF_DIV = 0x30;
        public const ushort BPF_OR = 0x40;
        public const ushort BPF_AND = 0x50;
        public const ushort BPF_LSH = 0x60;
        public const ushort BPF_RSH = 0x70;
        public const ushort BPF_NEG = 0x80;
        public const ushort BPF_MOD = 0x90;
        public const ushort BPF_XOR = 0xa0;

        public const ushort BPF_JA = 0x00;
        public const ushort BPF_JEQ = 0x10;
        public const ushort BPF_JGT = 0x20;
        public const ushort BPF_JGE = 0x30;
        public const ushort BPF_JSET = 0x40;

        public static ushort BPF_SRC(ushort code) => (ushort) (code & 0x08);
        public const ushort BPF_K = 0x00;
        public const ushort BPF_X = 0x08;

        public const ushort BPF_MAXINSNS = 4096;

        public static ushort BPF_RVAL(ushort code) => (ushort) (code & 0x18);
        public const ushort BPF_A = 0x10;

        public static ushort BPF_MISCOP(ushort code) => (ushort) (code & 0xf8);
        public const ushort BPF_TAX = 0x00;
        public const ushort BPF_TXA = 0x80;

        public static sock_filter BPF_STMT(ushort code, uint k) => new()
        {
            code = code,
            k = k
        };

        public static sock_filter BPF_JUMP(ushort code, uint k, byte jt, byte jf) => new()
        {
            code = code,
            jt = jt,
            jf = jf,
            k = k
        };

        public const int BPF_MEMWORDS = 16;

        public const uint SKF_AD_OFF = unchecked((uint) -0x1000);
        public const uint SKF_AD_PROTOCOL = 0;
        public const uint SKF_AD_PKTTYPE = 4;
        public const uint SKF_AD_IFINDEX = 8;
        public const uint SKF_AD_NLATTR = 12;
        public const uint SKF_AD_NLATTR_NEST = 16;
        public const uint SKF_AD_MARK = 20;
        public const uint SKF_AD_QUEUE = 24;
        public const uint SKF_AD_HATYPE = 28;
        public const uint SKF_AD_RXHASH = 32;
        public const uint SKF_AD_CPU = 36;
        public const uint SKF_AD_ALU_XOR_X = 40;
        public const uint SKF_AD_VLAN_TAG = 44;
        public const uint SKF_AD_VLAN_TAG_PRESENT = 48;
        public const uint SKF_AD_PAY_OFFSET = 52;
        public const uint SKF_AD_RANDOM = 56;
        public const uint SKF_AD_VLAN_TPID = 60;
        public const uint SKF_AD_MAX = 64;

        public const uint SKF_NET_OFF = unchecked((uint) -0x100000);
        public const uint SKF_LL_OFF = unchecked((uint) -0x200000);

        public const uint BPF_NET_OFF = SKF_NET_OFF;
        public const uint BPF_LL_OFF = SKF_LL_OFF;
    }
}