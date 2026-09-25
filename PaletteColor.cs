namespace GlobalGraffitiWall.API;

public class PaletteColor
{
    public byte Id { get; set; }
    public string HexCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
}
