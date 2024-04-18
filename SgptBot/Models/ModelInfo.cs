namespace SgptBot.Models;

public class ModelInfo(string internalName, string prettyName, Model modelEnum, string description)
{
    public string InternalName { get; set; } = internalName;
    public string PrettyName { get; set; } = prettyName;
    public Model ModelEnum { get; set; } = modelEnum;
    public string Description { get; set; } = description;
}