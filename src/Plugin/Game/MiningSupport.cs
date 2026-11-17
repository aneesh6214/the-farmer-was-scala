using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Core.Platform;
using Platforms;
using FarmerScala.Editor;
using FarmerScala.Runtime;

namespace FarmerScala.Game;

/// <summary>Read native ownership once at launch and select the available SDK.</summary>
internal static class MiningSupport
{
    internal static bool Ready { get; private set; }
    internal static void Initialize(string root)
    {
        if (Ready || PlatformManager.PlatformHandler == null) return;
        ResourceManager.LoadAll();
        bool mining = PlatformManager.IsDlcInstalled(EDlcType.Mining);
        var values = new List<(string Family, string Name, bool Mining)>();
        foreach (string family in new[] { "Entities", "Grounds", "Items", "Unlocks", "Hats" })
            foreach (var value in (PyConstBag)Scope.EvaluateConstant(family))
            {
                string full = value.ToString()!;
                bool requiresMining = value switch {
                    EntitySO entity => entity.dlc == EDlcType.Mining,
                    GroundSO ground => ground.dlc == EDlcType.Mining,
                    ItemSO item => item.dlc == EDlcType.Mining,
                    HatSO hat => hat.dlc == EDlcType.Mining,
                    UnlockSO unlock => unlock.dlc == EDlcType.Mining,
                    _ => throw new InvalidDataException("Unsupported native game constant.")
                };
                values.Add((family, full.Substring(full.IndexOf('.') + 1), requiresMining));
            }
        var catalog = SdkProfile.SelectCatalog(values, mining);
        catalog["Directions"] = new[] { "North", "East", "South", "West" };
        string sdk = SdkProfile.Build(Path.Combine(root, "sdk"), Path.Combine(BepInEx.Paths.CachePath, "FarmerScala", "sdk"), mining, catalog);
        SdkProfile.Activate(sdk, mining, catalog);
        if (mining) ScalaSyntaxHighlighter.RegisterBuiltins(SdkProfile.MiningActions);
        Ready = true;
    }
}
