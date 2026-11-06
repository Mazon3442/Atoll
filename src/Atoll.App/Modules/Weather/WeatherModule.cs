namespace Atoll.Modules.Weather;

public sealed class WeatherModule : IIslandModule
{
    private WeatherViewModel? _vm;
    public object? Panel => _vm;
    public void Start(ModuleContext context)
    {
        _vm = new WeatherViewModel(context.Settings);
        _vm.Start();
    }
    public void Dispose() => _vm?.Dispose();
}
