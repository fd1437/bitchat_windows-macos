using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BitChat.Core;
using Windows.Devices.Geolocation;
namespace BitChat.Windows;

public sealed class LocationPicker:Window
{
    public string? SelectedCell{get;private set;}
    public bool Teleported{get;private set;}=true;
    private readonly Canvas map=new(){Width=1000,Height=500,Background=new SolidColorBrush(Color.FromRgb(16,29,39))};
    private readonly ScaleTransform scale=new(1,1);private readonly TranslateTransform translate=new();
    private readonly TextBox coordinate=new(){Text="48.8566,2.3522",MinWidth=180};private readonly ComboBox precision=new(){ItemsSource=new[]{2,3,4,5,6,7},SelectedItem=6,MinWidth=70};
    private readonly TextBlock status=new(){Text="Click the map, scroll to zoom, or enter latitude,longitude",Foreground=Brushes.LightGray};
    private double lat=48.8566,lon=2.3522;
    public LocationPicker()
    {
        Title="Location channels";Width=1050;Height=670;MinWidth=700;MinHeight=450;Background=new SolidColorBrush(Color.FromRgb(13,17,20));Foreground=Brushes.WhiteSmoke;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(16)};Content=root;
        var tools=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,12)};DockPanel.SetDock(tools,Dock.Top);root.Children.Add(tools);
        tools.Children.Add(coordinate);tools.Children.Add(new TextBlock{Text="  precision  ",VerticalAlignment=VerticalAlignment.Center});tools.Children.Add(precision);
        var locate=new Button{Content="use device location",Padding=new Thickness(9),Margin=new Thickness(12,0,0,0)};tools.Children.Add(locate);locate.Click+=async(_,_)=>
        {
            try{var access=await Geolocator.RequestAccessAsync();if(access!=GeolocationAccessStatus.Allowed)throw new InvalidOperationException("Windows location access was not granted");var locator=new Geolocator{DesiredAccuracyInMeters=500};var position=await locator.GetGeopositionAsync();lat=position.Coordinate.Point.Position.Latitude;lon=position.Coordinate.Point.Position.Longitude;Teleported=false;coordinate.Text=$"{lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)}";Mark();}catch(Exception e){status.Text=e.Message;}
        };
        var join=new Button{Content="join channel",Padding=new Thickness(12),Margin=new Thickness(12,0,0,0)};tools.Children.Add(join);join.Click+=(_,_)=>
        {try{var p=coordinate.Text.Split(',');lat=double.Parse(p[0],CultureInfo.InvariantCulture);lon=double.Parse(p[1],CultureInfo.InvariantCulture);SelectedCell=Geohash.Encode(lat,lon,(int)precision.SelectedItem);DialogResult=true;}catch(Exception e){status.Text=e.Message;}};
        status.Margin=new Thickness(0,10,0,0);DockPanel.SetDock(status,Dock.Bottom);root.Children.Add(status);
        var viewport=new Border{ClipToBounds=true,Background=map.Background};root.Children.Add(viewport);viewport.Child=map;var transform=new TransformGroup();transform.Children.Add(scale);transform.Children.Add(translate);map.RenderTransform=transform;map.HorizontalAlignment=HorizontalAlignment.Left;map.VerticalAlignment=VerticalAlignment.Top;
        using var stream=typeof(LocationPicker).Assembly.GetManifestResourceStream("BitChat.Assets.world_borders.geojson")??throw new InvalidOperationException("Map data missing");
        using var json=JsonDocument.Parse(stream);foreach(var geometry in json.RootElement.GetProperty("geometries").EnumerateArray())
        {
            if(geometry.GetProperty("type").GetString()!="LineString")continue;var line=new Polyline{Stroke=new SolidColorBrush(Color.FromRgb(85,151,128)),StrokeThickness=0.8,IsHitTestVisible=false};
            foreach(var point in geometry.GetProperty("coordinates").EnumerateArray())line.Points.Add(new Point((point[0].GetDouble()+180)/360*1000,(90-point[1].GetDouble())/180*500));map.Children.Add(line);
        }
        map.MouseLeftButtonDown+=(_,e)=>{var point=e.GetPosition(map);lon=point.X/1000*360-180;lat=90-point.Y/500*180;Teleported=true;coordinate.Text=$"{lat.ToString("F6",CultureInfo.InvariantCulture)},{lon.ToString("F6",CultureInfo.InvariantCulture)}";Mark();};
        viewport.MouseWheel+=(_,e)=>{var position=e.GetPosition(viewport);double next=Math.Clamp(scale.ScaleX*(e.Delta>0?1.6:1/1.6),1,10000),ratio=next/scale.ScaleX;translate.X=position.X-(position.X-translate.X)*ratio;translate.Y=position.Y-(position.Y-translate.Y)*ratio;scale.ScaleX=scale.ScaleY=next;e.Handled=true;};
        map.MouseRightButtonDown+=(_,_)=>{scale.ScaleX=scale.ScaleY=1;translate.X=translate.Y=0;};
    }
    private void Mark()=>status.Text=$"{Geohash.Encode(lat,lon,(int)precision.SelectedItem)} · {lat:F5}, {lon:F5} · right-click to reset zoom";
}
