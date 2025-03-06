namespace FacilitaDiarios.Core.Models;

public class DataModel
{
    public string? order { get; set; }
    public string? url { get; set; }
    public List<ServantModel> servants { get; set; }

    public DataModel()
    {
        servants = new List<ServantModel>();
    }
}