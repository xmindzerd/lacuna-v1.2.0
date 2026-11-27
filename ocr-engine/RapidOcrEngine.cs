// Adapted from RapidAI rapidocr_onnxruntime 1.4.4 (Apache-2.0).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Clipper2Lib;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using CvPoint=OpenCvSharp.Point;

internal sealed class RapidOcrEngine : IDisposable
{
    private readonly InferenceSession detector,classifier,recognizer;
    private readonly string[] alphabet;
    internal RapidOcrEngine(string folder)
    {
        using var opts=new SessionOptions {IntraOpNumThreads=4,InterOpNumThreads=1};
        detector=new InferenceSession(Path.Combine(folder,"ch_PP-OCRv4_det_infer.onnx"),opts);
        classifier=new InferenceSession(Path.Combine(folder,"ch_ppocr_mobile_v2.0_cls_infer.onnx"),opts);
        recognizer=new InferenceSession(Path.Combine(folder,"ch_PP-OCRv4_rec_infer.onnx"),opts);
        alphabet=new[]{""}.Concat(File.ReadAllLines(Path.Combine(folder,"ppocr_keys_v1.txt"),Encoding.UTF8)).Append(" ").ToArray();
    }
    private static int Multiple32(double x)=>Math.Max(32,(int)Math.Round((int)x/32.0)*32);
    private static double Ratio(Mat m)=>(double)m.Width/m.Height;
    private static DenseTensor<float> Input(IReadOnlyList<Mat> images,int height,int width,bool fit)
    {
        var pixels=new float[checked(images.Count*3*height*width)];
        for(int n=0;n<images.Count;n++)
        {
            int w=fit? Math.Min(width,(int)Math.Ceiling(height*Ratio(images[n]))):width;
            using var small=new Mat();
            Cv2.Resize(images[n],small,new Size(w,height),0,0,InterpolationFlags.Linear);
            var bytes=new byte[checked(w*height*3)];
            Marshal.Copy(small.Data,bytes,0,bytes.Length);
            for(int c=0;c<3;c++)
            {
                int channel=(n*3+c)*width*height;
                for(int y=0;y<height;y++)
                    for(int x=0;x<w;x++)pixels[channel+y*width+x]=(bytes[(y*w+x)*3+c]/255f-.5f)/.5f;
            }
        }
        return new DenseTensor<float>(pixels,new[]{images.Count,3,height,width});
    }

    private static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Infer(InferenceSession s,DenseTensor<float> pixels)
        =>s.Run(new[]{NamedOnnxValue.CreateFromTensor(s.InputMetadata.Keys.First(),pixels)});

    private static Point2f[] Corners(RotatedRect rect)
    {
        var x=rect.Points().OrderBy(p=>p.X).ThenBy(p=>p.Y).ToArray();
        var left=x.Take(2).OrderBy(p=>p.Y).ToArray();
        var right=x.Skip(2).OrderBy(p=>p.Y).ToArray();
        return new[]{left[0],right[0],right[1],left[1]};
    }
    private static double BoxScore(Mat map,Point2f[] quad)
    {
        int x=Math.Clamp((int)Math.Floor(quad.Min(p=>p.X)),0,map.Width-1),y=Math.Clamp((int)Math.Floor(quad.Min(p=>p.Y)),0,map.Height-1);
        int r=Math.Clamp((int)Math.Ceiling(quad.Max(p=>p.X)),x,map.Width-1),b=Math.Clamp((int)Math.Ceiling(quad.Max(p=>p.Y)),y,map.Height-1);
        using var area=new Mat(map,new Rect(x,y,r-x+1,b-y+1));
        using var mask=new Mat(area.Size(),MatType.CV_8UC1,Scalar.Black);
        Cv2.FillPoly(mask,new[]{quad.Select(p=>new CvPoint((int)(p.X-x),(int)(p.Y-y))).ToArray()},Scalar.White);
        return Cv2.Mean(area,mask).Val0;
    }
    private List<Point2f[]> Detect(Mat page)
    {
        double scale=Math.Max(1,736d/Math.Min(page.Width,page.Height));
        // Bound the detector for unusually narrow panoramic pages.
        scale=Math.Min(scale,4096d/Math.Max(page.Width,page.Height));
        int w=Multiple32(page.Width*scale),h=Multiple32(page.Height*scale);
        using var inference=Infer(detector,Input(new[]{page},h,w,false));
        float[] prediction=inference.First().AsTensor<float>().ToArray();
        using var prob=Mat.FromPixelData(h,w,MatType.CV_32FC1,prediction);
        using var binary=new Mat();
        Cv2.Threshold(prob,binary,.3,255,ThresholdTypes.Binary);
        binary.ConvertTo(binary,MatType.CV_8UC1);
        using var kernel=new Mat(2,2,MatType.CV_8UC1,Scalar.All(1));
        Cv2.Dilate(binary,binary,kernel);
        Cv2.FindContours(binary,out CvPoint[][] contours,out _,RetrievalModes.List,ContourApproximationModes.ApproxSimple);
        var boxes=new List<Point2f[]>();
        foreach(var contour in contours.Take(1000))
        {
            var rect=Cv2.MinAreaRect(contour);
            if(Math.Min(rect.Size.Width,rect.Size.Height)<3)continue;
            var quad=Corners(rect);
            if(BoxScore(prob,quad)<.5)continue;
            var polygon=new Path64(quad.Select(p=>new Point64((long)p.X,(long)p.Y)));
            double perimeter=Cv2.ArcLength(quad,true);
            if(perimeter<=0)continue;
            var inflated=Clipper.InflatePaths(new Paths64{polygon},1.6*Cv2.ContourArea(quad)/perimeter,JoinType.Round,EndType.Polygon);
            if(inflated.Count!=1 || inflated[0].Count<3)continue;
            rect=Cv2.MinAreaRect(inflated[0].Select(p=>new CvPoint((int)p.X,(int)p.Y)).ToArray());
            if(Math.Min(rect.Size.Width,rect.Size.Height)<5)continue;
            var q=Corners(rect).Select(p=>new Point2f((int)Math.Clamp(Math.Round(p.X/w*page.Width),0,page.Width-1),
                (int)Math.Clamp(Math.Round(p.Y/h*page.Height),0,page.Height-1))).ToArray();
            if(q[0].DistanceTo(q[1])>=4 && q[0].DistanceTo(q[3])>=4)boxes.Add(q);
        }
        return boxes;
    }

    private static Mat Crop(Mat page,Point2f[] q)
    {
        int w=Math.Max(1,(int)Math.Max(q[0].DistanceTo(q[1]),q[2].DistanceTo(q[3])));
        int h=Math.Max(1,(int)Math.Max(q[0].DistanceTo(q[3]),q[1].DistanceTo(q[2])));
        using var transform=Cv2.GetPerspectiveTransform(q,new[]{new Point2f(0,0),new Point2f(w,0),new Point2f(w,h),new Point2f(0,h)});
        var crop=new Mat();
        Cv2.WarpPerspective(page,crop,transform,new Size(w,h),InterpolationFlags.Cubic,BorderTypes.Replicate);
        if(h>=1.5*w)Cv2.Rotate(crop,crop,RotateFlags.Rotate90Counterclockwise);
        return crop;
    }

    private void Decode(Mat[] crops,Point2f[][] boxes,List<Box> output)
    {
        using(var angles=Infer(classifier,Input(crops,48,192,true)))
        {
            var values=angles.First().AsTensor<float>();
            for(int i=0;i<crops.Length;i++)
                if(values[i,1]>.9f && values[i,1]>values[i,0])Cv2.Rotate(crops[i],crops[i],RotateFlags.Rotate180);
        }
        int width=Math.Clamp((int)(48*crops.Max(Ratio)),320,8192);
        using var result=Infer(recognizer,Input(crops,48,width,true));
        var prediction=result.First().AsTensor<float>();
        int steps=prediction.Dimensions[1],count=prediction.Dimensions[2];
        if(count!=alphabet.Length)throw new InvalidDataException("识别字典与模型不匹配。");
        var valuesRec=prediction.ToDenseTensor().Buffer.Span;
        for(int n=0;n<crops.Length;n++)
        {
            var word=new StringBuilder();float score=0;int length=0,prev=-1;
            for(int t=0;t<steps;t++)
            {
                var logits=valuesRec.Slice((n*steps+t)*count,count);
                int best=0;
                for(int k=1;k<count;k++)if(logits[k]>logits[best])best=k;
                if(best!=0 && best!=prev){word.Append(alphabet[best]);score+=logits[best];length++;}
                prev=best;
            }
            if(length==0 || score/length<.5f)continue;
            var q=boxes[n];
            output.Add(new Box(q.Min(p=>p.X),q.Min(p=>p.Y),q.Max(p=>p.X),q.Max(p=>p.Y),word.ToString(),score/length));
        }
    }
    internal Box[] Read(string path)
    {
        using var source=Cv2.ImDecode(File.ReadAllBytes(path),ImreadModes.Unchanged);
        using var original=new Mat();
        if(source.Empty())throw new InvalidDataException("页面图像无法解码。");
        if(source.Channels()==4)
        {
            using var bgr=new Mat();
            Cv2.CvtColor(source,bgr,ColorConversionCodes.BGRA2BGR);
            using var alpha=source.ExtractChannel(3);
            Cv2.BitwiseAnd(bgr,bgr,original,alpha);
            Scalar mean=Cv2.Mean(original);
            if(mean.Val0+mean.Val1+mean.Val2>0)Cv2.BitwiseNot(original,original);
            else {Cv2.BitwiseNot(alpha,alpha);Cv2.CvtColor(alpha,original,ColorConversionCodes.GRAY2BGR);}
        }
        else if(source.Channels()==1)Cv2.CvtColor(source,original,ColorConversionCodes.GRAY2BGR);
        else source.CopyTo(original);
        if(original.Empty())throw new InvalidDataException("页面图像无法解码。");
        using var page=new Mat();
        double scale=Math.Min(1,2000d/Math.Max(original.Width,original.Height));
        if(scale<1)Cv2.Resize(original,page,new Size(Multiple32(original.Width*scale),Multiple32(original.Height*scale)));
        else original.CopyTo(page);
        var regions=Detect(page);
        var lines=new List<(Point2f[] Box,Mat Crop)>();
        try
        {
            foreach(var q in regions)lines.Add((q,Crop(page,q)));
            var output=new List<Box>();
            foreach(var batch in lines.OrderBy(l=>Ratio(l.Crop)).Chunk(6))
                Decode(batch.Select(b=>b.Crop).ToArray(),batch.Select(b=>b.Box).ToArray(),output);
            return output.ToArray();
        }
        finally{foreach(var line in lines)line.Crop.Dispose();}
    }
    public void Dispose(){detector.Dispose();classifier.Dispose();recognizer.Dispose();}
}