using System.Buffers.Binary;
using System.Globalization;
namespace Workbench.Testing.Domain;

/// <summary>Passive 320x180 bottom-up BGR BMP, with a visible static SIMULATED banner. Unsupported glyphs use '?'.</summary>
public static class SimulatedFrameRenderer
{
    private static readonly Dictionary<char,string> Glyphs=new()
    {
        ['A']="01110100011000111111100011000110001",['B']="11110100011000111110100011000111110",['C']="01111100001000010000100001000001111",
        ['D']="11110100011000110001100011000111110",['E']="11111100001000011110100001000011111",['F']="11111100001000011110100001000010000",
        ['G']="01111100001000010111100011000101111",['H']="10001100011000111111100011000110001",['I']="11111001000010000100001000010011111",
        ['J']="00111000100001000010100101001001100",['K']="10001100101010011000101001001010001",['L']="10000100001000010000100001000011111",
        ['M']="10001110111010110101100011000110001",['N']="10001110011010110011100011000110001",['O']="01110100011000110001100011000101110",
        ['P']="11110100011000111110100001000010000",['Q']="01110100011000110001101011001001101",['R']="11110100011000111110101001001010001",
        ['S']="01111100001000001110000010000111110",['T']="11111001000010000100001000010000100",['U']="10001100011000110001100011000101110",
        ['V']="10001100011000110001100010101000100",['W']="10001100011000110101101011101110001",['X']="10001100010101000100010101000110001",
        ['Y']="10001100010101000100001000010000100",['Z']="11111000010001000100010001000011111",['0']="01110100011001110101110011000101110",
        ['1']="00100011000010000100001000010001110",['2']="01110100010000100010001000100011111",['3']="11110000010000101110000010000111110",
        ['4']="00010001100101010010111110001000010",['5']="11111100001000011110000010000111110",['6']="01110100001000011110100011000101110",
        ['7']="11111000010001000100010000100001000",['8']="01110100011000101110100011000101110",['9']="01110100011000101111000010000101110",
        ['?']="01110100010000100010001000000000100",[':']="00000001000010000000001000010000000",['.']="00000000000000000000000000010000100",
        ['-']="00000000000000011111000000000000000",[' ']="00000000000000000000000000000000000"
    };
    public static byte[] Render(VirtualTaskFormState state)
    {
        var bytes=new byte[172854];bytes[0]=(byte)'B';bytes[1]=(byte)'M';
        Int(2,172854);Int(10,54);Int(14,40);Int(18,320);Int(22,180);Short(26,1);Short(28,24);Int(34,172800);
        Rect(0,0,320,180,241,245,249);Rect(0,0,320,34,127,29,29);Text("SIMULATED",12,10,2,255,255,255);
        Text("VIRTUAL TASK FORM",12,45,1,15,23,42);Text("TITLE",12,63,1,51,65,85);
        Rect(12,75,296,23,255,255,255);Text(state.Title,17,82,1,15,23,42);
        Rect(12,108,62,22,30,64,175);Text("SAVE",27,115,1,255,255,255);
        Text("TOAST: "+state.Toast,12,140,1,15,23,42);Text("VALIDATION: "+state.Validation,12,151,1,127,29,29);
        Text("CREATED COUNT: "+state.CreatedCount.ToString(CultureInfo.InvariantCulture),12,166,1,15,23,42);
        return bytes;
        void Int(int offset,int value)=>BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset),value);
        void Short(int offset,short value)=>BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset),value);
        void Pixel(int x,int y,byte r,byte g,byte b)
        { if(x<0 || x>=320 || y<0 || y>=180) return;var index=54+(179-y)*960+x*3;bytes[index]=b;bytes[index+1]=g;bytes[index+2]=r; }
        void Rect(int x,int y,int width,int height,byte r,byte g,byte b)
        { for(var row=y;row<y+height;row++) for(var col=x;col<x+width;col++) Pixel(col,row,r,g,b); }
        void Text(string value,int x,int y,int scale,byte r,byte g,byte b)
        {
            var count=0;
            foreach(var rune in value.EnumerateRunes())
            {
                if(x+(count+1)*6*scale>308) break;
                var c=rune.IsAscii ? char.ToUpperInvariant((char)rune.Value) : '?';
                var pattern=Glyphs.GetValueOrDefault(c,Glyphs['?']);
                for(var row=0;row<7;row++) for(var col=0;col<5;col++) if(pattern[row*5+col]=='1') Rect(x+count*6*scale+col*scale,y+row*scale,scale,scale,r,g,b);
                count++;
            }
        }
    }
}
