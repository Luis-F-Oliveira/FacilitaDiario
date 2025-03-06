using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FacilitaDiarios.Core;
using FacilitaDiarios.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;

namespace FacilitaDiarios;

public static partial class Program
{
    [GeneratedRegex(@"([a-z])([A-Z])")]
    private static partial Regex SpacesBetweenRegex();

    [GeneratedRegex(@"(\d)([A-Z])")]
    private static partial Regex SpacesBetweenNumbersAndUppercaseRegex();

    [GeneratedRegex(@"([A-Z])([A-Z][a-z])")]
    private static partial Regex SpacesBetweenUppercaseGroupsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SingleSpacedRegex();
    
    public static async Task Main(string[] args)
    {
        Console.WriteLine("Iniciando Programa");
        
        // Iniciando o user secrets
        
        var config = new ConfigurationBuilder()
            .AddUserSecrets<UserSecrets>()
            .Build();
        
        // Iniciando http client para se conectar com o servidor
        
        using var client = new HttpClient();
        
        client.BaseAddress = new Uri(config["Api"]!);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config["ApiKey"]);
        
        // Buscando dados dos servidores via GET
        
        try
        {
            var response = await client.GetAsync("servants");
            if (response.IsSuccessStatusCode)
            {
                var servantsJson = await response.Content.ReadAsStringAsync();
                
                var servants = JsonSerializer.Deserialize<List<ServantModel>>(servantsJson);
                
                if (servants == null || servants.Count == 0)
                {
                    Console.WriteLine("Nenhum servidor encontrado na resposta.");
                    return;
                }
                
                var currentDate = DateTime.Now;
                var dateObject = new { date = currentDate.ToString("yyyy-MM-dd") };
                var jsonDate = JsonSerializer.Serialize(dateObject);
                var httpDate = new StringContent(jsonDate, Encoding.UTF8, "application/json");
                
                Console.WriteLine("Servidores Coletados");
                Console.WriteLine("Iniciando coletada na Iomat");
                
                // Iniciando sistema playwright para scrapping
        
                var playwright = await Playwright.CreateAsync();
                var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                });
                var page = await browser.NewPageAsync();
                await page.GotoAsync("https://www.iomat.mt.gov.br/");

                // Acessando menu lateral referente a Defensoria Pública

                await page.Locator("xpath=//*[@id=\'downloadPdf\']").ClickAsync();
                await page.Locator("xpath=//*[@id=\'html-interna\']/a").ClickAsync();

                await page.WaitForTimeoutAsync(3000);

                var element = page.GetByRole(AriaRole.Listitem).Filter(new LocatorFilterOptions
                {
                    HasText = "DEFENSORIA PÚBLICA"
                });
                await element.ClickAsync();

                var listItems = element.Locator("ul li");
                await listItems.First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible
                });
                var itemCount = await listItems.CountAsync();

                // Navegando entre as portarias e coletando dados

                var dataList = new List<DataModel>();
                
                for (var i = 0; i < itemCount; i++)
                {
                    var listItem = listItems.Nth(i);
                    await listItem.Locator("a").ClickAsync();

                    await page.WaitForTimeoutAsync(3000);

                    var order = await page.Locator("xpath=//*[@id=\'info\']/strong[3]").TextContentAsync();
                    var url = page.Url;

                    var iframeElement = page.Locator("xpath=//*[@id=\'view_materia\']");
                    var iframe = iframeElement.ContentFrame;

                    await page.WaitForTimeoutAsync(3000);

                    var textLocator = iframe.Locator("xpath=/html/body");
                    var textContent = await textLocator.TextContentAsync();

                    if (textContent is null) continue;
                    
                    var formattedText = Formatted(textContent);
                        
                    var foundServants = FindServantId(formattedText, servants);

                    var data = new DataModel
                    {
                        order = order,
                        url = url,
                        servants = foundServants
                    };
                    
                    dataList.Add(data);
                }
                
                var jsonContent = JsonSerializer.Serialize(dataList);
                var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                
                await client.PostAsync("save", httpContent);
                await client.PostAsync("notify", httpDate);
                
                Console.WriteLine("Dados salvos, enviando e-mails");
                
                await browser.CloseAsync();
                
                Console.WriteLine("Finalizado Programa");
            }
            else
            {
                Console.WriteLine($"Error: {(int)response.StatusCode} - {response.ReasonPhrase}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception: {ex.Message}");
        }
    }
    
    private static string Formatted(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // 1. Normalizar o texto e remover diacríticos (acentos)
    
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized.Where(
                     c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
        {
            sb.Append(c);
        }
        var withoutDiacritics = sb.ToString();
    
        // 2. Adicionar espaços entre letras minúsculas seguidas de maiúsculas
    
        var addSpacesBetweenCases = SpacesBetweenRegex().Replace(
            withoutDiacritics, "$1 $2");
    
        // 3. Adicionar espaços entre números seguidos de letras maiúsculas
    
        var addSpacesBetweenNumbersAndUppercase = SpacesBetweenNumbersAndUppercaseRegex().Replace(
            addSpacesBetweenCases, "$1 $2");

        // 4. Adicionar espaços entre letras maiúsculas seguidas por outras maiúsculas seguidas de minúsculas
    
        var addSpacesBetweenUppercaseGroups = SpacesBetweenUppercaseGroupsRegex().Replace(
            addSpacesBetweenNumbersAndUppercase, "$1 $2");

        // 5. Substituir múltiplos espaços por um único espaço
    
        var singleSpaced = SingleSpacedRegex().Replace(
            addSpacesBetweenUppercaseGroups, " ");

        // 6. Converter para maiúsculas e remover espaços extras nas extremidades
    
        return singleSpaced.ToUpper().Trim();
    }

    private static List<ServantModel> FindServantId(string text, List<ServantModel> servants)
    {
        return servants.Where(servant => servant.name != null && text.Contains(servant.name, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
