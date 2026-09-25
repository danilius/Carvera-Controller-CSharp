using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Carvera.App.Layout;
using Carvera.Core.Commands;
using Carvera.Core.Connection;
using Carvera.Core.Expressions;
using Carvera.Core.State;
using Carvera.Layout;

namespace Carvera.App.Components;

/// <summary>Connection kind, address/port, network discovery and connect/disconnect.</summary>
public static class ConnectionComponent
{
    private static readonly (string Key, string Label)[] AllKinds = [("wifi", "Wi-Fi"), ("usb", "USB"), ("simulator", "Simulator")];

    public static Control Create(LayoutNode node, ComponentHost host, BuildContext ctx)
    {
        var offered = node.GetStringList("kinds").Select(k => k.ToLowerInvariant()).ToList();
        var kinds = AllKinds.Where(k => offered.Count == 0 || offered.Contains(k.Key)).ToList();
        var settings = ctx.Services.Settings;

        var kind = new ComboBox { VerticalAlignment = VerticalAlignment.Center, MinWidth = 100 };
        foreach (var (_, label) in kinds) kind.Items.Add(label);
        var address = new AutoCompleteBox
        {
            PlaceholderText = "Machine IP address",
            MinWidth = 150,
            VerticalAlignment = VerticalAlignment.Center,
            Text = settings.ConnectionAddress,
            FilterMode = AutoCompleteFilterMode.None,
            MinimumPrefixLength = 0,
        };

        string SelectedKind() => kind.SelectedIndex >= 0 ? kinds[kind.SelectedIndex].Key : "wifi";
        void Store()
        {
            using var _ = ctx.State.BeginBatch();
            ctx.State.Set(StatePaths.ConnectionKind, SelectedKind());
            ctx.State.Set(StatePaths.ConnectionAddress, address.Text?.Trim());
        }

        void KindChanged()
        {
            var k = SelectedKind();
            address.IsVisible = k != "simulator";
            address.PlaceholderText = k == "usb" ? "COM port" : "Machine IP address";
            address.ItemsSource = k == "usb" ? SerialMachineStream.AvailablePorts() : null;
            if (k == "usb" && string.IsNullOrWhiteSpace(address.Text)) address.Text = SerialMachineStream.AvailablePorts().FirstOrDefault();
            Store();
        }
        var initial = kinds.FindIndex(k => k.Key == settings.ConnectionKind);
        kind.SelectedIndex = initial >= 0 ? initial : 0;
        kind.SelectionChanged += (_, _) => KindChanged();
        address.TextChanged += (_, _) => Store();
        KindChanged();

        ComponentHost Button(string key, string text, string? image, string command)
        {
            var visuals = new JsonObject();
            if (image is not null) visuals["normal"] = new JsonObject { ["image"] = image };
            var b = new ComponentHost(new LayoutNode("button", new JsonObject(), $"{node.Path}.{key}", []), ctx, "button", visuals);
            b.SetDefaultText(TextTemplate.Parse(text));
            ButtonComponents.AttachCommand(b, ctx, command, () => CommandArgs.Empty);
            b.Child = new VisualContent(b, ctx.Images);
            return b;
        }
        var connect = Button("connect", "Connect", "builtin:plug", "connect");
        var disconnect = Button("disconnect", "Disconnect", null, "disconnect");
        connect.Clicked += () =>
        {
            settings.ConnectionKind = SelectedKind();
            settings.ConnectionAddress = address.Text?.Trim() ?? "";
            settings.Save();
        };

        var discover = new Button { Content = "Find", VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(discover, "Listen for Carvera machines on the local network");
        discover.Click += async (_, _) =>
        {
            discover.IsEnabled = false;
            discover.Content = "Searching…";
            try
            {
                var machines = await MachineDetector.DiscoverAsync(TimeSpan.FromSeconds(3));
                if (machines.Count == 0) ctx.Console.Info("No machines answered on the local network.");
                foreach (var m in machines) ctx.Console.Info($"Found {m.Name} at {m.Endpoint}{(m.Busy ? " (busy)" : "")}.");
                address.ItemsSource = machines.Select(m => m.Endpoint).ToArray();
                if (machines.Count > 0) address.Text = machines[0].Endpoint;
            }
            catch (Exception ex)
            {
                ctx.Console.Error($"Machine discovery failed: {ex.Message}");
            }
            finally
            {
                discover.IsEnabled = true;
                discover.Content = "Find";
            }
        };

        void UpdateConnected()
        {
            var connected = ctx.State.Get(StatePaths.Connected, false);
            connect.IsVisible = !connected;
            disconnect.IsVisible = connected;
            kind.IsEnabled = address.IsEnabled = !connected;
            discover.IsVisible = !connected && SelectedKind() == "wifi";
        }
        kind.SelectionChanged += (_, _) => UpdateConnected();
        ctx.Watch([StatePaths.Connected], UpdateConnected);
        UpdateConnected();

        var vertical = node.GetString("orientation")?.Equals("vertical", StringComparison.OrdinalIgnoreCase) == true;
        var panel = new FlexPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal, Spacing = 6 };
        void Add(Control c, SizeSpec main)
        {
            FlexPanel.SetSlot(c, vertical ? new SlotSpec(SizeSpec.Fill, SizeSpec.Auto) : new SlotSpec(main, SizeSpec.Auto, VAlign: SlotAlign.Center));
            panel.Children.Add(c);
        }
        Add(kind, SizeSpec.Auto);
        Add(address, SizeSpec.Fill);
        Add(discover, SizeSpec.Auto);
        Add(connect, SizeSpec.Auto);
        Add(disconnect, SizeSpec.Auto);
        return panel;
    }
}
