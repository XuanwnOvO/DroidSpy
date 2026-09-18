using Android.App;
using Android.Runtime;
using Google.Android.Material.Color;

namespace DroidSpy;

[Application(Label = "@string/app_name", LargeHeap = true)]
public class DroidSpyApp : Application
{
    public DroidSpyApp(nint handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public override void OnCreate()
    {
        base.OnCreate();
        // 跟随系统壁纸取色的 Material You 动态配色
        DynamicColors.ApplyToActivitiesIfAvailable(this);
    }
}
