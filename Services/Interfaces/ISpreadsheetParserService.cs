namespace ADManagerAPI.Services.Interfaces;

public interface ISpreadsheetParserService
{
    /// <summary>
    ///     Détermine si ce parser peut traiter le format de fichier spécifié
    /// </summary>
    /// <param name="fileExtension">L'extension du fichier à traiter</param>
    /// <returns>Vrai si le service peut parser ce type de fichier, faux sinon</returns>
    bool CanHandle(string fileExtension);

    /// <summary>
    ///     Parse le contenu d'un fichier CSV ou Excel
    /// </summary>
    /// <param name="stream">Stream du fichier</param>
    /// <param name="fileName">Nom du fichier incluant l'extension</param>
    /// <param name="delimiter">Délimiteur pour les fichiers CSV</param>
    /// <param name="columns">Colonnes manuelles définies pour la configuration</param>
    /// <returns>Liste de dictionnaires représentant les données du tableau</returns>
    Task<List<Dictionary<string, string>>> ParseAsync(Stream stream, string fileName, string delimiter = ",",
        List<string>? columns = null);
}