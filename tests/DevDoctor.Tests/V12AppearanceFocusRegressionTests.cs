using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using DevDoctor.App;

namespace DevDoctor.Tests;

public sealed class V12AppearanceFocusRegressionTests
{
    [Fact]
    public void Theme_focus_lookup_rejects_invalidated_parameter_and_keeps_the_named_theme_target()
    {
        Exception? failure = null;
        var thread = new Thread(() => {
            try {
                var panel = new StackPanel();
                var close = new Button { Content = "关闭" };
                var theme = new Button { Content = "流光蓝", CommandParameter = "FluentBlue" };
                var invalid = new Button { Content = "无效项", CommandParameter = "not-a-preset" };
                panel.Children.Add(close); panel.Children.Add(theme); panel.Children.Add(invalid);
                var lookup = typeof(MainWindow).GetMethod("FindThemeButton", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(lookup);
                Assert.Null(lookup.Invoke(null, [panel, null]));
                Assert.Null(lookup.Invoke(null, [panel, "not-a-preset"]));
                Assert.Same(theme, lookup.Invoke(null, [panel, "FluentBlue"]));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA 查找测试未在限定时间内完成。");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
