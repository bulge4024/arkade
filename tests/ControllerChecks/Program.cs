using ArcadeOS.Services;
var root=Path.Combine(Path.GetTempPath(),"Arkade-controller-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try
{
 Check(ControllerInputService.DecodeAxes(0,0)==CabinetInput.None,"Neutral stick moved");
 Check(ControllerInputService.DecodeAxes(-1,0)==CabinetInput.Left && ControllerInputService.DecodeAxes(0,1)==CabinetInput.Down,"XY axes wrong");
 Check(ControllerInputService.DecodeAxes(0,0,9000)==CabinetInput.Right && ControllerInputService.DecodeAxes(0,0,0)==CabinetInput.Up,"POV hat wrong");
 Check(ControllerInputService.DecodeAxes(.2,-.2)==0,"Dead zone missing");
 var repeat=new CabinetInputRepeater();Check(repeat.Advance(CabinetInput.Select,0)==0,"Held startup button fired");repeat.Advance(0,10);
 Check(repeat.Advance(CabinetInput.Select,20)==CabinetInput.Select && repeat.Advance(CabinetInput.Select,1000)==0,"Select repeated while held");
 repeat.Advance(0,1100);Check(repeat.Advance(CabinetInput.Down,1200)==CabinetInput.Down,"Initial direction missing");
 Check(repeat.Advance(CabinetInput.Down,1500)==0 && repeat.Advance(CabinetInput.Down,1601)==CabinetInput.Down,"Directional repeat wrong");
 repeat.Reset();Check(repeat.Advance(CabinetInput.Start,1700)==0,"Focus return launched game");repeat.Advance(0,1800);
 Check(repeat.Advance(CabinetInput.Up|CabinetInput.Right,1900)==CabinetInput.Up,"Diagonal caused two moves");
 var reader=new FakeReader();var path=Path.Combine(root,"controllers.json");var service=new ControllerInputService(reader,path);
 var arcade=new ControllerReading("joy:1","USB Arcade","joystick",0,0);var pad=new ControllerReading("joy:2","PS-style pad","joystick",0,0);
 reader.Devices=[arcade,pad];service.Poll(true,0);
 service.SaveBindings(arcade.MappingKey,new(3,4,8,7));service.Poll(true,10);
 reader.Devices=[arcade with {Buttons=4},pad];Check(service.Poll(true,20)==CabinetInput.Select,"Arcade custom button failed");
 reader.Devices=[arcade,pad];service.Poll(true,30);
 reader.Devices=[arcade,pad with {Buttons=1}];Check(service.Poll(true,40)==CabinetInput.Select,"Mapping arcade altered gamepad defaults");
 var reloaded=new ControllerInputService(reader,path);Check(reloaded.GetBindings(arcade.MappingKey).Select==3 && reloaded.GetBindings(pad.MappingKey).Select==1,"Device mappings not preserved separately");
 service.Poll(false,50);reader.Devices=[arcade,pad];service.Poll(true,60);service.BeginCapture();
 reader.Devices=[arcade with {Buttons=8},pad];service.Poll(false,70);var capture=service.CaptureButton();Check(capture?.Kind==arcade.MappingKey && capture?.Button==4,"Button learning failed");
 Check(service.CaptureButton()==null,"Held button captured twice");
 reader.Devices=[];Check(service.Poll(true,80)==0,"Disconnect produced input");
 Console.WriteLine("PASS: axes, POV/D-pad, dead zone, single-step buttons, direction repeat, held-button safety, two independent controller mappings, persistence, button learning and disconnect. No system input was generated.");
}
finally{Directory.Delete(root,true);}
static void Check(bool b,string message){if(!b)throw new Exception(message);}
class FakeReader:IControllerReader{public IReadOnlyList<ControllerReading> Devices=[];public IReadOnlyList<ControllerReading> Read()=>Devices;}
