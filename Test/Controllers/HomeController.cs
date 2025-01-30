using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace EasyMath.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly string _blobConnectionString;
        private readonly string _blobContainerName;

        public HomeController(ILogger<HomeController> logger, IConfiguration configuration)
        {
            _logger = logger;
            _blobConnectionString = configuration["AzureBlobStorage:ConnectionString"];
            _blobContainerName = configuration["AzureBlobStorage:ContainerName"];
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public IActionResult Solve(string equation)
        {
            if (string.IsNullOrWhiteSpace(equation))
            {
                ViewData["Result"] = "Introduceti o ecuatie valida.";
                return View("Index");
            }

            try
            {
                var result = VerificaSiRezolvaEcuatie(equation);
                ViewData["Result"] = result;
            }
            catch (Exception ex)
            {
                ViewData["Result"] = $"A aparut o eroare: {ex.Message}";
            }

            return View("Index");
        }

        public static string VerificaSiRezolvaEcuatie(string s)
        {
            s = s.Replace(" ", "");

            string variable = IdentifyVariable(s);
            if (string.IsNullOrEmpty(variable))
                throw new ArgumentException("Nu s-a gasit nicio variabila in ecuatie.");

            var parts = s.Split('=');
            if (parts.Length != 2)
                throw new ArgumentException("Ecuatia nu este valida.");

            double coefX2 = 0;
            double coefX = 0;
            double constanta = 0;

            ParseEquationSide(parts[0], variable, ref coefX2, ref coefX, ref constanta);

            ParseEquationSide(parts[1], variable, ref coefX2, ref coefX, ref constanta, inverse: true);

            if (Math.Abs(coefX2) > 1e-9)
            {
                return RezolvareEcuatieGradDoi(coefX2, coefX, constanta);
            }
            else if (Math.Abs(coefX) > 1e-9)
            {
                return RezolvareEcuatieGradUnu(coefX, constanta);
            }
            else
            {
                if (Math.Abs(constanta) < 1e-9)
                    return "Ecuatia are solutii infinite.";
                else
                    return "Ecuatia nu are solutii.";
            }
        }

        public static string RezolvareEcuatieGradDoi(double a, double b, double c)
        {
            if (Math.Abs(a) < 1e-9)
                throw new ArgumentException("Coeficientul lui x^2 (a) trebuie sa fie diferit de 0.");

            double delta = b * b - 4 * a * c;

            if (Math.Abs(delta) < 1e-9)
            {
                double x = -b / (2 * a);
                return $"O solutie reala: x = {x}";
            }
            else if (delta > 0)
            {
                double x1 = (-b + Math.Sqrt(delta)) / (2 * a);
                double x2 = (-b - Math.Sqrt(delta)) / (2 * a);
                return $"Doua solutii reale: x1 = {x1}, x2 = {x2}";
            }
            else
            {
                double realPart = -b / (2 * a);
                double imaginaryPart = Math.Sqrt(-delta) / (2 * a);
                return $"Doua solutii complexe: x1 = {realPart} + {imaginaryPart}i, x2 = {realPart} - {imaginaryPart}i";
            }
        }

        public static string RezolvareEcuatieGradUnu(double coefX, double constanta)
        {
            if (Math.Abs(coefX) < 1e-9)
            {
                if (Math.Abs(constanta) < 1e-9)
                    return "Ecuatia are solutii infinite.";
                else
                    return "Ecuatia nu are solutii.";
            }

            double x = -constanta / coefX;
            return $"Solutia: x = {x}";
        }

        private static double ParseCoefficient(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "+") return 1;
            if (s == "-") return -1;
            return double.Parse(s);
        }

        private static void ParseEquationSide(string side, string variable, ref double coefVar2, ref double coefVar, ref double constanta, bool inverse = false)
        {
            string regexPattern = $@"([-+]?\d*\.?\d*){variable}2|([-+]?\d*\.?\d*){variable}|([-+]?\d*\.?\d+)";
            var regex = new Regex(regexPattern);
            var matches = regex.Matches(side);

            foreach (Match match in matches)
            {
                if (match.Groups[1].Success)
                {
                    double value = ParseCoefficient(match.Groups[1].Value);
                    coefVar2 += inverse ? -value : value;
                }
                else if (match.Groups[2].Success)
                {
                    double value = ParseCoefficient(match.Groups[2].Value);
                    coefVar += inverse ? -value : value;
                }
                else if (match.Groups[3].Success)
                {
                    double value = ParseCoefficient(match.Groups[3].Value);
                    constanta += inverse ? -value : value;
                }
            }
        }

        private static string IdentifyVariable(string equation)
        {
            var match = Regex.Match(equation, @"[a-zA-Z]");
            return match.Success ? match.Value : null;
        }

        [HttpPost]
        public async Task<IActionResult> Upload(IFormFile uploadedImage)
        {
            if (uploadedImage == null || uploadedImage.Length == 0)
            {
                ViewData["Result"] = "Nu a fost incarcata nicio imagine.";
                return View("Index");
            }

            try
            {
                // Salveaza imaginea temporar
                var tempFilePath = Path.GetTempFileName();
                using (var stream = new FileStream(tempFilePath, FileMode.Create))
                {
                    await uploadedImage.CopyToAsync(stream);
                }

                // Detecteaza textul din imagine
                string extractedText = await ExtractTextFromImage(tempFilePath);

                // Sterge fisierul temporar
                System.IO.File.Delete(tempFilePath);

                if (string.IsNullOrEmpty(extractedText))
                {
                    ViewData["Result"] = "Nu s-a putut detecta text din imagine.";
                    return View("Index");
                }

                // Rezolva ecuatia
                var result = VerificaSiRezolvaEcuatie(extractedText);

                // Salveaza imaginea si rezultatul in Azure Blob Storage
                string blobName = CleanBlobName(Guid.NewGuid().ToString());

                await SaveToBlobStorage(uploadedImage, blobName, result);

                ViewData["Result"] = result;
                return View("Index");
            }
            catch (Exception ex)
            {
                ViewData["Result"] = $"A aparut o eroare: {ex.Message}";
                return View("Index");
            }
        }

        private async Task<string> ExtractTextFromImage(string imagePath)
        {
            // Configurare client Azure Computer Vision
            var endpoint = "https://easymath.cognitiveservices.azure.com/";
            var subscriptionKey = "BD4I7RKPxi79IOXmaZPKZjsoFbuSfv0QeXZ6V3uI3G0JSviBme11JQQJ99BAAC5RqLJXJ3w3AAAFACOGOugB";

            var client = new ComputerVisionClient(new ApiKeyServiceClientCredentials(subscriptionKey))
            {
                Endpoint = endpoint
            };

            // Citește imaginea și extrage textul utilizând OCR
            using (var imageStream = new FileStream(imagePath, FileMode.Open))
            {
                var ocrResult = await client.ReadInStreamAsync(imageStream);
                var operationId = ocrResult.OperationLocation.Split('/').Last();

                // Asteapta rezultatul procesarii
                ReadOperationResult results;
                do
                {
                    await Task.Delay(1000);
                    results = await client.GetReadResultAsync(Guid.Parse(operationId));
                }
                while (results.Status == OperationStatusCodes.Running || results.Status == OperationStatusCodes.NotStarted);

                // Extrage textul detectat
                if (results.Status == OperationStatusCodes.Succeeded)
                {
                    var text = string.Join(" ", results.AnalyzeResult.ReadResults
                        .SelectMany(result => result.Lines)
                        .Select(line => line.Text));
                    return text;
                }
            }

            return null;
        }

        [HttpGet]
        public async Task<IActionResult> History()
        {
            try
            {
                var history = await GetHistoryFromBlobStorage();

                if (history == null || !history.Any())
                {
                    ViewData["Error"] = "Nu exista intrari in istoric.";
                    return View("History", new List<(string Url, string Result)>());
                }

                return View("History", history);
            }
            catch (Exception ex)
            {
                ViewData["Error"] = $"A aparut o eroare la incarcarea istoricului: {ex.Message}";
                return View("Index");
            }
        }


        private async Task SaveToBlobStorage(IFormFile file, string blobName, string result)
        {
            // Creeaza clientul pentru Blob Container
            var blobServiceClient = new BlobServiceClient(_blobConnectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient(_blobContainerName);

            // Curata numele blob-ului pentru a elimina caracterele invalide
            blobName = CleanBlobName(blobName);

            // Salveaza imaginea cu Content-Type setat
            var blobClient = containerClient.GetBlobClient(blobName);
            using (var stream = file.OpenReadStream())
            {
                await blobClient.UploadAsync(stream, new BlobHttpHeaders
                {
                    ContentType = file.ContentType // Seteaza Content-Type in functie de tipul fisierului (image/png, image/jpeg, etc.)
                });
            }

            // Seteaza metadata pentru rezultat
            await blobClient.SetMetadataAsync(new Dictionary<string, string>
            {
                { "Result", result }
            });
        }


        // Functie pentru curatarea numelui blob-ului
        private string CleanBlobName(string blobName)
        {
            // Inlocuim caracterele invalide cu '_'
            blobName = Regex.Replace(blobName, @"[^a-zA-Z0-9\-]", "_");
            return blobName.ToLowerInvariant(); // Convertim la lowercase pentru consistenta
        }


        private async Task<List<(string Url, string Result)>> GetHistoryFromBlobStorage()
        {
            var blobServiceClient = new BlobServiceClient(_blobConnectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient(_blobContainerName);

            var history = new List<(string Url, string Result)>();

            await foreach (BlobItem blobItem in containerClient.GetBlobsAsync())
            {
                System.Console.WriteLine("tura");
                var blobClient = containerClient.GetBlobClient(blobItem.Name);

                // Inițializează rezultatul cu un fallback
                string result = "Rezultat necunoscut";
                var properties = await blobClient.GetPropertiesAsync();
                if (properties.Value.Metadata != null && properties.Value.Metadata.Any())
                {
                    
                    foreach (var metadata in properties.Value.Metadata)
                    { 
                        result= metadata.Value;System.Console.WriteLine(result); 
                    }

                }
                history.Add((Url: blobClient.Uri.ToString(), Result: result));
            }

            return history;
        }





    }
}
