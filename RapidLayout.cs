using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OneClickFilm
{
    internal static class RapidLayout
    {
        public sealed class Box
        {
            public double Left {get;set;} public double Top {get;set;}
            public double Right {get;set;} public double Bottom {get;set;}
            public string Text {get;set;}
            internal double Center {get{return (Top+Bottom)/2;}}
            internal double Height {get{return Math.Max(1,Bottom-Top);}}
        }
        public sealed class Page
        {
            public int Number {get;set;}
            public bool Blank {get;set;}
            public Box[] Boxes {get;set;}
        }
        internal static ScriptPdfPage ReadPage(string json)
        {
            var page=AgentData.Json().Deserialize<Page>(json);
            if(page==null || page.Boxes==null)throw new InvalidDataException("RapidOCR 未返回文字框。");
            var rows=new List<List<Box>>();
            foreach(var box in page.Boxes.Where(x=>x!=null && !String.IsNullOrWhiteSpace(x.Text)).OrderBy(x=>x.Center).ThenBy(x=>x.Left))
            {
                var row=rows.LastOrDefault();
                if(row==null || Math.Abs(box.Center-row.Average(b=>b.Center))>.55*Math.Max(box.Height,row.Average(b=>b.Height)))
                {row=new List<Box>();rows.Add(row);}
                row.Add(box);
            }
            var lines=new List<string>();
            foreach(var row in rows)
            {
                var line=new StringBuilder();
                Box previous=null;
                foreach(var box in row.OrderBy(b=>b.Left))
                {
                    if(previous!=null && box.Left-previous.Right>.35*previous.Height)line.Append(' ');
                    line.Append(box.Text);
                    previous=box;
                }
                lines.Add(TitleSpacing(line.ToString()));
            }
            return new ScriptPdfPage{Number=page.Number,Blank=page.Blank,Language="RapidOCR · PP-OCRv4",Text=String.Join("\n",lines)};
        }
        internal static string TitleSpacing(string line)
        {
            // Only a bracketed English book / film title qualifies. Names and dialogue retain their spelling.
            return Regex.Replace(line,@"《([A-Za-z][A-Za-z '’\-]{2,120})》",m=>
            {
                string input=Regex.Replace(m.Groups[1].Value,@"\btothe(?=[A-Z])","to the ");
                string title=Regex.Replace(input,@"\b[A-Z][a-z]+(?:[A-Z][a-z]+)+\b",word=>
                    Regex.IsMatch(word.Value,@"^(?:Mc|Mac)[A-Z]") ? word.Value :
                        Regex.Replace(word.Value,@"(?<=[a-z])(?=[A-Z][a-z])"," "));
                title=Regex.Replace(title,@"\btothe(?= [A-Z])","to the");
                return "《"+title+"》";
            });
        }
    }
}