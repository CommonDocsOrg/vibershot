using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
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
  EditorChecks.Run(first);
  Console.WriteLine("PASS: overlap and pixels, duplicate rejection, AVI frame counts/chunk sizes, legacy settings defaults, WebP lossy/lossless encoding, editor history/operations/project/flatten.");
 }
}
static class EditorChecks {
 static void Check(bool ok,string what){if(!ok)throw new Exception(what);}
 public static void Run(BitmapSource image){
  // Legacy config without an Editor section gets the defaults; a saved section reads back.
  var legacy=JsonSerializer.Deserialize<AppConfig>("{\"Autosave\":true}")!;Check(legacy.Editor.DefaultFontFamily=="Segoe UI"&&legacy.Editor.DefaultFontSize==18&&legacy.Editor.DefaultStrokeColor=="#F0612A"&&legacy.Editor.DefaultStrokeWidth==3&&!legacy.Editor.OpenEditorAfterCapture,"Editor settings defaults");
  var editorCfg=JsonSerializer.Deserialize<AppConfig>("{\"Editor\":{\"OpenEditorAfterCapture\":true,\"DefaultStrokeWidth\":5}}")!;Check(editorCfg.Editor.OpenEditorAfterCapture&&editorCfg.Editor.DefaultStrokeWidth==5,"Editor settings not read");
  var cfgJson=JsonSerializer.Serialize(new AppConfig());Check(cfgJson.Contains("\"Editor\""),"Editor settings not written");

  // History: undo/redo, coalescing, rollback, depth limit, dirty tracking.
  var h=new Vibershot.Editing.History<string>("a",limit:3);
  h.Push("b");h.Push("c","drag");h.Push("d","drag");h.Push("e","drag");
  Check(h.Current=="e"&&h.UndoDepth==2,"Coalesced pushes should form one step");
  Check(h.Undo()=="b","Undo of a coalesced drag should restore the pre-drag state");
  Check(h.Redo()=="e","Redo should restore the end of the drag");
  h.BreakCoalescing();h.Push("f","drag");Check(h.UndoDepth==3,"Breaking coalescing should start a new step");
  h.Push("g");Check(h.UndoDepth==3,"Depth limit not enforced");
  Check(h.Undo()=="f"&&h.Undo()=="e"&&h.Undo()=="b"&&!h.CanUndo,"Oldest entry should have been dropped");
  Check(h.Redo()=="e"&&h.CanRedo,"Redo chain broken");
  h.Push("x");Check(!h.CanRedo,"Push should clear redo");
  h.Push("y","nudge");h.Rollback("nudge");Check(h.Current=="x"&&!h.CanRedo,"Rollback should drop the coalesced step");
  h.Rollback("nudge");Check(h.Current=="x","Rollback with a stale key should do nothing");
  h.MarkSaved();Check(!h.IsDirty,"MarkSaved");h.Push("z");Check(h.IsDirty,"Dirty after push");h.Undo();Check(!h.IsDirty,"Undo back to saved state should be clean");
  int changes=0;h.Changed+=()=>changes++;h.Push("q");h.Undo();Check(changes==2,"Changed event count");

  // Document operations.
  var assets=new Vibershot.Editing.AssetStore();
  var doc=Vibershot.Editing.DocumentOps.FromImage(image,assets);
  Check(doc.Size==new Size(120,100)&&doc.Layers.Count==2&&doc.Layers[0].Items[0] is Vibershot.Editing.ImageItem,"FromImage layout");
  Check(Vibershot.Editing.DocumentOps.ActiveLayerIndex(doc)==1,"Active layer should be the annotation layer");
  var rect=new Vibershot.Editing.RectItem{Bounds=new Rect(10,10,40,30),Stroke=Colors.Red,StrokeWidth=3,CornerRadius=4};
  var ellipse=new Vibershot.Editing.EllipseItem{Bounds=new Rect(20,20,10,10),Fill=Colors.Blue};
  var arrow=new Vibershot.Editing.ArrowItem{Stroke=Colors.Black,StrokeWidth=2,HeadSize=10}.WithPoints(new Point(100,80),new Point(5,5));
  var text=new Vibershot.Editing.TextItem{Bounds=new Rect(5,60,100,30),Text="Hello",FontFamily="Arial",FontSize=20,Bold=true,Italic=true,Underline=true,Color=Colors.Green,Alignment=Vibershot.Editing.TextAlign.Center,Background=Colors.Yellow,Padding=4,Rotation=15,Opacity=0.5,Shadow=true,Stroke=Colors.Gray,StrokeWidth=1};
  var doc2=Vibershot.Editing.DocumentOps.AddItems(doc,new Vibershot.Editing.Item[]{rect,ellipse,arrow,text});
  Check(doc.Layers[1].Items.Count==0&&doc2.Layers[1].Items.Count==4,"AddItems must not mutate the input");
  Check(arrow.Reversed&&arrow.Flipped==false&&arrow.Head==new Point(5,5)&&arrow.Tail==new Point(100,80),"Arrow direction");
  var moved=Vibershot.Editing.DocumentOps.MoveItems(doc2,new[]{rect.Id,ellipse.Id},new Vector(5,-5));
  Check(Vibershot.Editing.DocumentOps.FindItem(moved,rect.Id)!.Bounds==new Rect(15,5,40,30)&&Vibershot.Editing.DocumentOps.FindItem(moved,ellipse.Id)!.Bounds==new Rect(25,15,10,10)&&Vibershot.Editing.DocumentOps.FindItem(moved,text.Id)!.Bounds==text.Bounds,"MoveItems");
  var resized=Vibershot.Editing.DocumentOps.SetBounds(doc2,rect.Id,new Rect(0,0,7,8));Check(Vibershot.Editing.DocumentOps.FindItem(resized,rect.Id)!.Bounds==new Rect(0,0,7,8),"SetBounds");
  var updated=Vibershot.Editing.DocumentOps.UpdateItems(doc2,new[]{rect.Id,text.Id},i=>i with{StrokeWidth=9,Id=Guid.Empty});
  Check(Vibershot.Editing.DocumentOps.FindItem(updated,rect.Id)!.StrokeWidth==9&&Vibershot.Editing.DocumentOps.FindItem(updated,text.Id)!.StrokeWidth==9&&Vibershot.Editing.DocumentOps.FindItem(updated,ellipse.Id)!.StrokeWidth==0,"UpdateItems should keep ids and skip others");
  var removed=Vibershot.Editing.DocumentOps.RemoveItems(doc2,new[]{ellipse.Id});Check(removed.Layers[1].Items.Count==3&&Vibershot.Editing.DocumentOps.FindItem(removed,ellipse.Id)==null,"RemoveItems");
  Guid[] Order(Vibershot.Editing.ImageDocument d)=>d.Layers[1].Items.Select(i=>i.Id).ToArray();
  Check(Order(Vibershot.Editing.DocumentOps.Reorder(doc2,rect.Id,Vibershot.Editing.ZOrderMove.BringToFront)).SequenceEqual(new[]{ellipse.Id,arrow.Id,text.Id,rect.Id}),"BringToFront");
  Check(Order(Vibershot.Editing.DocumentOps.Reorder(doc2,text.Id,Vibershot.Editing.ZOrderMove.SendToBack)).SequenceEqual(new[]{text.Id,rect.Id,ellipse.Id,arrow.Id}),"SendToBack");
  Check(Order(Vibershot.Editing.DocumentOps.Reorder(doc2,rect.Id,Vibershot.Editing.ZOrderMove.BringForward)).SequenceEqual(new[]{ellipse.Id,rect.Id,arrow.Id,text.Id}),"BringForward");
  Check(Order(Vibershot.Editing.DocumentOps.Reorder(doc2,arrow.Id,Vibershot.Editing.ZOrderMove.SendBackward)).SequenceEqual(new[]{rect.Id,arrow.Id,ellipse.Id,text.Id}),"SendBackward");
  Check(ReferenceEquals(Vibershot.Editing.DocumentOps.Reorder(doc2,rect.Id,Vibershot.Editing.ZOrderMove.SendToBack),doc2),"No-op reorder should return the same document");
  var cropped=Vibershot.Editing.DocumentOps.SetCrop(doc2,new Rect(10,20,200,30));Check(cropped.CropRect==new Rect(10,20,110,30),"Crop should clamp to the canvas");
  Check(Vibershot.Editing.DocumentOps.SetCrop(doc2,new Rect(0,0,120,100)).CropRect==null&&Vibershot.Editing.DocumentOps.SetCrop(cropped,null).CropRect==null,"Full or null crop should clear");
  var rotatedBounds=Vibershot.Editing.DocumentOps.VisualBounds(new Vibershot.Editing.RectItem{Bounds=new Rect(0,0,10,10),Rotation=90});Check(Math.Abs(rotatedBounds.X)<0.001&&Math.Abs(rotatedBounds.Width-10)<0.001,"VisualBounds of a 90 degree square");
  var clones=Vibershot.Editing.DocumentOps.Clone(new[]{rect},new Vector(10,10));Check(clones[0].Id!=rect.Id&&clones[0].Bounds==new Rect(20,20,40,30),"Clone");

  // JSON polymorphism: every item kind round trips with its discriminator and properties.
  var itemsJson=Vibershot.Editing.ItemClipboard.Serialize(new Vibershot.Editing.Item[]{rect,ellipse,arrow,text,doc.Layers[0].Items[0]});
  Check(itemsJson.Contains("\"type\": \"rect\"")&&itemsJson.Contains("\"type\": \"arrow\"")&&itemsJson.Contains("\"type\": \"text\"")&&itemsJson.Contains("\"type\": \"image\"")&&itemsJson.Contains("\"type\": \"ellipse\""),"Discriminators missing");
  var back=Vibershot.Editing.ItemClipboard.Deserialize(itemsJson);Check(back.Count==5&&back[0] is Vibershot.Editing.RectItem&&back[1] is Vibershot.Editing.EllipseItem&&back[2] is Vibershot.Editing.ArrowItem&&back[3] is Vibershot.Editing.TextItem&&back[4] is Vibershot.Editing.ImageItem,"Polymorphic items");
  Check(back[0].Equals(rect)&&back[1].Equals(ellipse)&&back[2].Equals(arrow)&&back[3].Equals(text),"Item properties changed in JSON round trip");
  Check(Vibershot.Editing.DocumentJson.ParseColor("#F0612A")==Color.FromRgb(0xF0,0x61,0x2A)&&Vibershot.Editing.DocumentJson.ColorToString(Colors.Red)=="#FFFF0000","Color parsing");

  // Project file round trip keeps the document, every item property and the asset pixels.
  var project=Vibershot.Editing.DocumentOps.SetCrop(doc2,new Rect(10,10,60,50)) with{Background=Colors.White};
  project=Vibershot.Editing.DocumentOps.SetLayer(project,1,project.Layers[1] with{Name="Notes",Opacity=0.75,Locked=true,Visible=false});
  var projectPath=Path.Combine(AppContext.BaseDirectory,"roundtrip.vibeshot");Vibershot.Editing.ProjectFile.Save(projectPath,project,assets);
  var (loaded,loadedAssets)=Vibershot.Editing.ProjectFile.Load(projectPath);
  Check(loaded.Size==project.Size&&loaded.CropRect==project.CropRect&&loaded.Background==project.Background&&loaded.Layers.Count==2,"Project document fields");
  Check(loaded.Layers[1].Name=="Notes"&&loaded.Layers[1].Opacity==0.75&&loaded.Layers[1].Locked&&!loaded.Layers[1].Visible&&loaded.Layers[1].Id==project.Layers[1].Id,"Project layer fields");
  var originalItems=project.AllItems.ToList();var loadedItems=loaded.AllItems.ToList();Check(loadedItems.Count==originalItems.Count,"Project item count");
  for(int i=0;i<originalItems.Count;i++)Check(loadedItems[i].Equals(originalItems[i]),"Project item "+i+" changed: "+originalItems[i].GetType().Name);
  var baseAsset=((Vibershot.Editing.ImageItem)loaded.Layers[0].Items[0]).AssetId;var loadedBitmap=loadedAssets.Get(baseAsset);Check(loadedBitmap!=null&&loadedBitmap.PixelWidth==120&&loadedBitmap.PixelHeight==100,"Project asset missing");
  byte[] original=new byte[120*100*4];image.CopyPixels(original,480,0);byte[] restored=new byte[120*100*4];new FormatConvertedBitmap(loadedBitmap,PixelFormats.Bgra32,null,0).CopyPixels(restored,480,0);Check(original.AsSpan().SequenceEqual(restored),"Project asset pixels changed");

  // Flatten honours the crop and renders items; a tall document renders in bands.
  var flat=Vibershot.Editing.DocumentRenderer.Flatten(Vibershot.Editing.DocumentOps.SetCrop(doc,new Rect(10,20,50,40)),assets);
  Check(flat.PixelWidth==50&&flat.PixelHeight==40,"Flatten should use the crop size");
  byte[] flatPixels=new byte[50*40*4];flat.CopyPixels(flatPixels,200,0);byte[] srcRow=new byte[480];image.CopyPixels(new Int32Rect(0,20,120,1),srcRow,480,0);
  for(int x=0;x<50;x++)for(int c=0;c<3;c++)Check(flatPixels[x*4+c]==srcRow[(x+10)*4+c],"Flatten pixel mismatch at x="+x);
  var full=Vibershot.Editing.DocumentRenderer.Flatten(doc,assets);Check(full.PixelWidth==120&&full.PixelHeight==100,"Flatten without crop should be the canvas size");
  var withFill=Vibershot.Editing.DocumentOps.AddItem(Vibershot.Editing.DocumentOps.Blank(new Size(20,20),Colors.Transparent),new Vibershot.Editing.RectItem{Bounds=new Rect(0,0,20,20),Fill=Colors.Red});
  var fillFlat=Vibershot.Editing.DocumentRenderer.Flatten(withFill,assets);byte[] fp=new byte[20*20*4];fillFlat.CopyPixels(fp,80,0);Check(fp[2]==255&&fp[1]==0&&fp[0]==0&&fp[3]==255,"Filled rect should render red");
  var tall=Vibershot.Editing.DocumentOps.AddItem(Vibershot.Editing.DocumentOps.Blank(new Size(4,Vibershot.Editing.DocumentRenderer.BandHeight+10),Colors.Transparent),new Vibershot.Editing.RectItem{Bounds=new Rect(0,Vibershot.Editing.DocumentRenderer.BandHeight+5,4,5),Fill=Colors.Blue});
  var tallFlat=Vibershot.Editing.DocumentRenderer.Flatten(tall,assets);Check(tallFlat.PixelHeight==Vibershot.Editing.DocumentRenderer.BandHeight+10,"Banded flatten height");
  byte[] lastRow=new byte[16];tallFlat.CopyPixels(new Int32Rect(0,tallFlat.PixelHeight-1,4,1),lastRow,16,0);Check(lastRow[0]==255&&lastRow[2]==0&&lastRow[3]==255,"Banded flatten should render the last band");
  byte[] firstRow=new byte[16];tallFlat.CopyPixels(new Int32Rect(0,0,4,1),firstRow,16,0);Check(firstRow[3]==0,"Banded flatten first row should be transparent");
  var itemsFlat=Vibershot.Editing.DocumentRenderer.FlattenItems(new[]{rect},assets);Check(itemsFlat.PixelWidth==48&&itemsFlat.PixelHeight==38,"FlattenItems size should be the item bounds plus margin");
  var measured=Vibershot.Editing.DocumentRenderer.MeasureText(text);Check(measured.Width>8&&measured.Height>20,"MeasureText");
 }
}
