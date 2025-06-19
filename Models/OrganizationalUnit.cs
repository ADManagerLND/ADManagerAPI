namespace ADManagerAPI.Models;

public class OrganizationalUnitModel
{
    public string Name { get; set; }
    public string DistinguishedName { get; set; }
    public string Description { get; set; }
    public List<OrganizationalUnitModel> ChildOUs { get; set; } = [];
}