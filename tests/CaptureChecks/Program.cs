using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vibershot.Core;
class Program {
 [STAThread] static void Main() {
  BitmapSource Frame(int offset) { int w=120,h=100; byte[] p=new byte[w*h*4]; for(int y=0;y<h;y++)for(int x=0;x<w;x++){int i=(y*w+x)*4; p[i]=(byte)((x*17+(y+offset)*7)%256);p[i+1]=(byte)((x*3+(y+offset)*13)%256);p[i+2]=(byte)((x*11+(y+offset)*19)%256);p[i+3]=255;} var b=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,p,w*4);b.Freeze();return b; }
  var first=Frame(0);var next=Frame(40);var stitched=ScrollStitcher.Append(first,first,next);if(stitched.PixelHeight!=140)throw new Exception("Wrong overlap");
  var expected=Frame(0); byte[] pixels=new byte[120*140*4];stitched.CopyPixels(pixels,480,0);byte[] tail=new byte[480*100];next.CopyPixels(tail,480,0);for(int i=0;i<480*40;i++)if(pixels[480*100+i]!=tail[480*60+i])throw new Exception("Wrong stitched pixels");
  bool rejected=false;try{ScrollStitcher.Append(first,first,first);}catch(InvalidOperationException){rejected=true;}if(!rejected)throw new Exception("Duplicate accepted");
  var path=Path.Combine(AppContext.BaseDirectory,"test.avi");File.Delete(path);using(var writer=new VideoWriter(path,120,100)){writer.AddFrame(first);using var recovery=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);byte[] checkpoint=new byte[checked((int)recovery.Length)];recovery.ReadExactly(checkpoint);if(BitConverter.ToInt32(checkpoint,4)!=checkpoint.Length-8||BitConverter.ToInt32(checkpoint,48)!=1)throw new Exception("Invalid recovery checkpoint");writer.AddFrame(next);}
  byte[] avi=File.ReadAllBytes(path);if(Encoding.ASCII.GetString(avi,0,4)!="RIFF"||BitConverter.ToInt32(avi,4)!=avi.Length-8||BitConverter.ToInt32(avi,48)!=2)throw new Exception("Invalid AVI header");
  int pos=12;while(pos<avi.Length){string id=Encoding.ASCII.GetString(avi,pos,4);int size=BitConverter.ToInt32(avi,pos+4);if(size<0||pos+8+size>avi.Length)throw new Exception("Invalid AVI chunk");pos+=8+size+(size&1);}if(pos!=avi.Length)throw new Exception("Bad AVI size");
  var config=JsonSerializer.Deserialize<AppConfig>("{\"Autosave\":true}");if(!config.ShowCaptureActions||config.ShowSaveDialog)throw new Exception("Legacy config defaults");
  if(config.PrintScreen!=PrintScreenAction.Off)throw new Exception("Print Screen should be off by default");
  var ps=JsonSerializer.Deserialize<AppConfig>("{\"PrintScreen\":\"FullDesktop\"}");if(ps.PrintScreen!=PrintScreenAction.FullDesktop)throw new Exception("Print Screen setting not read");
  if(config.WebpQuality!=80)throw new Exception("WebP quality default");
  var legacyWebp=JsonSerializer.Deserialize<AppConfig>("{\"Format\":2}");if(legacyWebp.Format!=ImageFormat.Webp)throw new Exception("Legacy WebP enum value changed");
  foreach(var (fmt,lossless) in new[]{(ImageFormat.Webp,false),(ImageFormat.WebpLossless,true)}){
   var ms=new MemoryStream();ImageEncoding.Encode(first,fmt,60,ms);byte[] webp=ms.ToArray();
   if(webp.Length<12||Encoding.ASCII.GetString(webp,0,4)!="RIFF"||Encoding.ASCII.GetString(webp,8,4)!="WEBP")throw new Exception("Invalid WebP header for "+fmt);
   string chunk=Encoding.ASCII.GetString(webp,12,4);if(lossless?chunk!="VP8L":chunk!="VP8 ")throw new Exception("Unexpected WebP chunk "+chunk+" for "+fmt);
   using var decoded=SkiaSharp.SKBitmap.Decode(webp);if(decoded is null||decoded.Width!=120||decoded.Height!=100)throw new Exception("WebP size mismatch for "+fmt);
   if(lossless){byte[] orig=new byte[120*100*4];first.CopyPixels(orig,480,0);for(int y=0;y<100;y++)for(int x=0;x<120;x++){var c=decoded.GetPixel(x,y);int i=(y*120+x)*4;if(c.Blue!=orig[i]||c.Green!=orig[i+1]||c.Red!=orig[i+2]||c.Alpha!=orig[i+3])throw new Exception("Lossless WebP changed pixels");}}
   if(ImageEncoding.Extension(fmt)!="webp")throw new Exception("WebP extension");
  }
  Console.WriteLine("PASS: overlap and pixels, duplicate rejection, AVI frame counts/chunk sizes, legacy settings defaults, WebP lossy/lossless encoding.");
 }
}
