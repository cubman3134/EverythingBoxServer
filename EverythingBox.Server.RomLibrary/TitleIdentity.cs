namespace EverythingBox.Server.RomLibrary;

public enum TitleKind { Base, Update, Dlc }

/// <summary>What a single console file is. TitleId is the GROUP KEY (the base program id / game code);
/// Kind places the file within its title; Version orders updates (higher = newer), null if unknown.</summary>
public sealed record PackageIdentity(string TitleId, TitleKind Kind, int? Version);
