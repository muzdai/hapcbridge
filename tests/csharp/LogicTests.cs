using System.Text.Json.Nodes;
using HaPcBridge.Common;
using HaPcBridge.Service.Sensors;
using Xunit;

public class RemoteConfigTests
{
    static BridgeConfig Fresh()
    {
        var c = new BridgeConfig { DeviceId = "pc" };
        ConfigStore.Normalize(c);
        return c;
    }

    [Fact]
    public void Applies_allowed_keys_and_merges_sections()
    {
        var rejected = new List<string>();
        var patch = JsonNode.Parse("""{"updateIntervalSeconds":5,"temperatureUnit":"F","sensors":{"idleTime":false},"commands":{"notifications":false,"shutdownDelaySeconds":45}}""")!.AsObject();
        var next = RemoteConfig.Apply(Fresh(), patch, rejected);

        Assert.Empty(rejected);
        Assert.Equal(5, next.UpdateIntervalSeconds);
        Assert.True(next.IsFahrenheit);
        Assert.False(next.Sensors.IdleTime);
        Assert.True(next.Sensors.CpuLoad);          // untouched keys keep their value
        Assert.False(next.Commands.Notifications);
        Assert.Equal(45, next.Commands.ShutdownDelaySeconds);
    }

    [Fact]
    public void Never_changes_broker_or_identity()
    {
        var rejected = new List<string>();
        var patch = JsonNode.Parse("""{"mqtt":{"host":"evil"},"deviceId":"x","baseTopic":"y","allowRemoteConfig":false,"sensors":{"nope":true}}""")!.AsObject();
        var cfg = Fresh();
        var next = RemoteConfig.Apply(cfg, patch, rejected);

        Assert.Equal(cfg.Mqtt.Host, next.Mqtt.Host);
        Assert.Equal("pc", next.DeviceId);
        Assert.Equal("hapcbridge", next.BaseTopic);
        Assert.True(next.AllowRemoteConfig);
        Assert.Equal(new[] { "mqtt", "deviceId", "baseTopic", "allowRemoteConfig", "sensors.nope" }, rejected);
    }

    [Fact]
    public void Public_view_has_no_broker_section()
    {
        var cfg = Fresh();
        cfg.Mqtt.Password = "secret";
        var json = RemoteConfig.Public(cfg).ToJsonString();
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("\"mqtt\"", json);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(10, 10)]
    [InlineData(99999, 3600)]
    public void Normalize_clamps_interval(int given, int expected)
    {
        var c = new BridgeConfig { UpdateIntervalSeconds = given };
        ConfigStore.Normalize(c);
        Assert.Equal(expected, c.UpdateIntervalSeconds);
    }

    [Theory]
    [InlineData("Gaming PC", "gaming_pc")]
    [InlineData("  ПК-1 ", "1")]
    [InlineData("***", "pc")]
    public void Slug_is_topic_safe(string name, string expected) => Assert.Equal(expected, ConfigStore.Slug(name));
}

public class EstimatorTests
{
    static readonly PowerConfig Power = new() { BaselineWatts = 60, PsuEfficiency = 0.9 };

    static HardwareReading Hw(double? cpu, params GpuReading[] gpus)
    {
        var h = new HardwareReading { CpuPower = cpu };
        h.Gpus.AddRange(gpus);
        return h;
    }

    [Fact]
    public void Psu_telemetry_wins_over_estimate()
    {
        var h = Hw(70, new GpuReading { Vendor = "nvidia", Power = 280 });
        h.PsuPower = 455; h.PsuName = "Corsair HX1000i";
        var (w, src) = Estimator.TotalPower(h, Power);
        Assert.Equal(455, w);
        Assert.Equal("psu: Corsair HX1000i", src);
    }

    [Fact]
    public void Estimate_adds_discrete_gpu_but_not_the_igpu()
    {
        var igpu = new GpuReading { Vendor = "amd", Name = "Radeon Graphics", Power = 15, MemoryTotalGb = 0.5 };
        var dgpu = new GpuReading { Vendor = "nvidia", Name = "RTX 4080", Power = 280, MemoryTotalGb = 16 };
        var (w, src) = Estimator.TotalPower(Hw(70, igpu, dgpu), Power);
        Assert.Equal("estimate", src);
        Assert.Equal((70 + 280) / 0.9 + 60, w!.Value, 3);
    }

    [Fact]
    public void No_sensors_means_unavailable_not_baseline()
    {
        var (w, src) = Estimator.TotalPower(Hw(null), Power);
        Assert.Null(w);
        Assert.Equal("unavailable", src);
    }

    [Fact]
    public void Primary_gpu_prefers_name_then_nvidia_then_vram()
    {
        var igpu = new GpuReading { Vendor = "amd", Name = "AMD Radeon(TM) Graphics", MemoryTotalGb = 0.5 };
        var rx = new GpuReading { Vendor = "amd", Name = "AMD Radeon RX 7900 XT", MemoryTotalGb = 20 };
        var rtx = new GpuReading { Vendor = "nvidia", Name = "NVIDIA GeForce RTX 4080", MemoryTotalGb = 16 };

        Assert.Same(rx, Estimator.PrimaryGpu(new[] { igpu, rx }, ""));
        Assert.Same(rtx, Estimator.PrimaryGpu(new[] { igpu, rx, rtx }, null));
        Assert.Same(rx, Estimator.PrimaryGpu(new[] { igpu, rx, rtx }, "7900"));
        Assert.Same(rtx, Estimator.PrimaryGpu(new[] { igpu, rx, rtx }, "no such card"));
        Assert.Null(Estimator.PrimaryGpu(Array.Empty<GpuReading>(), ""));
    }

    [Fact]
    public void Temperature_conversion()
    {
        Assert.Equal(212, Estimator.ToDisplayTemperature(100, fahrenheit: true));
        Assert.Equal(58.5, Estimator.ToDisplayTemperature(58.5, fahrenheit: false));
        Assert.Null(Estimator.ToDisplayTemperature(null, true));
    }
}

public class NotificationTests
{
    [Fact]
    public void Plain_text_is_the_message()
    {
        var n = Notification.Parse("Стирка закончилась")!;
        Assert.Equal("Стирка закончилась", n.Message);
        Assert.Empty(n.Actions);
    }

    [Fact]
    public void Json_with_actions()
    {
        var n = Notification.Parse("""{"title":"Дверь","message":"Звонок","tag":"door","duration":"long","silent":true,"actions":[{"id":"open","title":"Открыть"},"Игнорировать",{"action":"x"},1,2,3,4]}""")!;
        Assert.Equal("Дверь", n.Title);
        Assert.Equal("door", n.Tag);
        Assert.True(n.Long);
        Assert.True(n.Silent);
        Assert.Equal(5, n.Actions.Count); // capped at five
        Assert.Equal(("open", "Открыть"), (n.Actions[0].Id, n.Actions[0].Title));
        Assert.Equal(("Игнорировать", "Игнорировать"), (n.Actions[1].Id, n.Actions[1].Title));
        Assert.Equal(("x", "x"), (n.Actions[2].Id, n.Actions[2].Title));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{broken")]
    [InlineData("""{"title":""}""")]
    public void Rejects_empty_or_broken(string payload) => Assert.Null(Notification.Parse(payload));

    [Fact]
    public void Survives_the_trip_to_the_agent()
    {
        var n = Notification.Parse("""{"message":"hi","actions":[{"id":"a","title":"A"}]}""")!;
        var back = Notification.FromJson(n.ToJson())!;
        Assert.Equal("hi", back.Message);
        Assert.Equal("a", back.Actions[0].Id);
    }
}
