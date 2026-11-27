using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record Box(float Left,float Top,float Right,float Bottom,string Text,float Confidence);
[JsonSerializable(typeof(Box[]))]
internal partial class OcrJson : JsonSerializerContext { }

internal static class Program
{
    // Reuse all three CPU inference sessions for the lifetime of one imported PDF.
    private static int Main()
    {
        Console.InputEncoding=new UTF8Encoding(false);
        Console.OutputEncoding=new UTF8Encoding(false);
        try
        {
            using var engine=new RapidOcrEngine(AppContext.BaseDirectory);
            string page;
            while((page=Console.ReadLine())!=null)
                Console.WriteLine(JsonSerializer.Serialize(engine.Read(page),OcrJson.Default.BoxArray));
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error.GetBaseException().Message);return 1;}
    }
}