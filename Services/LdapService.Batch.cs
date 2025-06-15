using System.DirectoryServices.Protocols;
using ADManagerAPI.Models;
using SearchScope = System.DirectoryServices.Protocols.SearchScope;

namespace ADManagerAPI.Services
{
    /// <summary>
    /// Extension du LdapService avec les méthodes batch optimisées pour les performances
    /// </summary>
    public partial class LdapService
    {
        #region Méthodes batch optimisées pour l'analyse de performances

        /// <summary>
        /// Récupère plusieurs utilisateurs en une seule requête LDAP optimisée
        /// </summary>
        public async Task<List<UserModel>> GetUsersBatchAsync(List<string> samAccountNames)
        {
            if (!samAccountNames?.Any() == true)
                return new List<UserModel>();

            var results = new List<UserModel>();
            const int batchSize = 100; // Limiter la taille des requêtes LDAP

            try
            {
                if (!await EnsureConnectionAsync())
                {
                    _logger.LogWarning("⚠️ GetUsersBatchAsync: LDAP indisponible, retour liste vide");
                    return results;
                }

                // Diviser en lots pour éviter les requêtes LDAP trop grandes
                for (int i = 0; i < samAccountNames.Count; i += batchSize)
                {
                    var batch = samAccountNames.Skip(i).Take(batchSize).ToList();
                    var batchResults = await GetUsersBatchInternalAsync(batch);
                    results.AddRange(batchResults);
                }

                _logger.LogInformation($"✅ Chargement batch: {results.Count} utilisateurs trouvés sur {samAccountNames.Count} recherchés");
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Erreur lors du chargement batch de {samAccountNames.Count} utilisateurs");
                return results; // Retourner ce qui a été trouvé plutôt que de lever l'exception
            }
        }

        /// <summary>
        /// Requête LDAP interne pour un lot d'utilisateurs
        /// </summary>
        private async Task<List<UserModel>> GetUsersBatchInternalAsync(List<string> samAccountNames)
        {
            var results = new List<UserModel>();

            return await Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(_baseDn))
                    {
                        _logger.LogWarning("GetUsersBatchInternalAsync impossible: _baseDn est null");
                        return results;
                    }

                    // Construire un filtre LDAP pour rechercher plusieurs utilisateurs
                    var samFilters = samAccountNames.Select(sam => $"(sAMAccountName={EscapeLdapValue(sam)})");
                    var combinedFilter = $"(&(objectClass=user)(objectCategory=person)(|{string.Join("", samFilters)}))";
                    
                    var req = new SearchRequest(_baseDn, combinedFilter, SearchScope.Subtree, new[] { 
                        "sAMAccountName", "displayName", "distinguishedName", 
                        "givenName", "sn", "mail", "userPrincipalName",
                        "department", "title", "telephoneNumber", "description"
                    });
                    req.SizeLimit = 2000; // Limite de sécurité
                    
                    var res = (SearchResponse)_connection.SendRequest(req);
                    
                    foreach (SearchResultEntry entry in res.Entries)
                    {
                        try
                        {
                            var user = CreateUserModelFromSearchResult(entry);
                            if (user != null)
                            {
                                results.Add(user);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, $"Erreur lors de la création d'un UserModel depuis SearchResult");
                        }
                    }

                    _logger.LogDebug($"Batch interne: {results.Count} utilisateurs extraits pour {samAccountNames.Count} demandés");
                    return results;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Erreur dans GetUsersBatchInternalAsync pour {samAccountNames.Count} utilisateurs");
                    return results;
                }
            });
        }

        /// <summary>
        /// Recherche avancée d'utilisateurs avec filtre personnalisé
        /// </summary>
        public async Task<List<UserModel>> SearchUsersAsync(string searchBase, string ldapFilter)
        {
            var results = new List<UserModel>();

            try
            {
                if (!await EnsureConnectionAsync())
                {
                    _logger.LogWarning("⚠️ SearchUsersAsync: LDAP indisponible, retour liste vide");
                    return results;
                }

                return await Task.Run(() =>
                {
                    try
                    {
                        var req = new SearchRequest(searchBase, ldapFilter, SearchScope.Subtree, new[] { 
                            "sAMAccountName", "displayName", "distinguishedName", 
                            "givenName", "sn", "mail", "userPrincipalName"
                        });
                        req.SizeLimit = 5000; // Limite plus élevée pour la recherche

                        var res = (SearchResponse)_connection.SendRequest(req);
                        
                        foreach (SearchResultEntry entry in res.Entries)
                        {
                            try
                            {
                                var user = CreateUserModelFromSearchResult(entry);
                                if (user != null)
                                {
                                    results.Add(user);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Erreur lors de la création d'un UserModel depuis SearchResult");
                            }
                        }

                        _logger.LogInformation($"🔍 Recherche LDAP avec filtre '{ldapFilter}': {results.Count} utilisateurs trouvés");
                        return results;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"❌ Erreur dans SearchUsersAsync pour '{ldapFilter}'");
                        return results;
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Erreur lors de la recherche d'utilisateurs par filtre '{ldapFilter}'");
                return results;
            }
        }

        /// <summary>
        /// Récupère les attributs de plusieurs utilisateurs en batch
        /// </summary>
        public async Task<Dictionary<string, Dictionary<string, string>>> GetUsersAttributesBatchAsync(
            List<string> userIdentifiers, 
            List<string> attributeNames)
        {
            var results = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            if (!userIdentifiers?.Any() == true || !attributeNames?.Any() == true)
                return results;

            try
            {
                if (!await EnsureConnectionAsync())
                {
                    _logger.LogWarning("⚠️ GetUsersAttributesBatchAsync: LDAP indisponible, retour dictionnaire vide");
                    return results;
                }

                return await Task.Run(() =>
                {
                    try
                    {
                        if (string.IsNullOrEmpty(_baseDn))
                        {
                            _logger.LogWarning("GetUsersAttributesBatchAsync impossible: _baseDn est null");
                            return results;
                        }

                        // Construire un filtre pour les utilisateurs demandés
                        var userFilters = userIdentifiers.Select(id => $"(sAMAccountName={EscapeLdapValue(id)})");
                        var combinedFilter = $"(&(objectClass=user)(objectCategory=person)(|{string.Join("", userFilters)}))";
                        
                        // Ajouter les attributs demandés + sAMAccountName pour l'indexation
                        var attributesToLoad = new List<string> { "sAMAccountName" };
                        attributesToLoad.AddRange(attributeNames);
                        
                        var req = new SearchRequest(_baseDn, combinedFilter, SearchScope.Subtree, attributesToLoad.Distinct().ToArray());
                        req.SizeLimit = 2000;

                        var res = (SearchResponse)_connection.SendRequest(req);
                        
                        foreach (SearchResultEntry entry in res.Entries)
                        {
                            try
                            {
                                var samAccountName = entry.Attributes.Contains("sAMAccountName") && entry.Attributes["sAMAccountName"].Count > 0
                                    ? entry.Attributes["sAMAccountName"][0]?.ToString()
                                    : null;
                                
                                if (string.IsNullOrEmpty(samAccountName)) continue;

                                var userAttributes = new Dictionary<string, string>();
                                
                                foreach (var attrName in attributeNames)
                                {
                                    if (entry.Attributes.Contains(attrName) && entry.Attributes[attrName].Count > 0)
                                    {
                                        userAttributes[attrName] = entry.Attributes[attrName][0]?.ToString() ?? "";
                                    }
                                    else
                                    {
                                        userAttributes[attrName] = ""; // Attribut absent = valeur vide
                                    }
                                }
                                
                                results[samAccountName] = userAttributes;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Erreur lors de l'extraction des attributs d'un utilisateur");
                            }
                        }

                        _logger.LogInformation($"📋 Attributs batch: {results.Count} utilisateurs traités pour {attributeNames.Count} attributs");
                        return results;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Erreur dans GetUsersAttributesBatchAsync");
                        return results;
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Erreur lors de la récupération batch des attributs");
                return results;
            }
        }

        /// <summary>
        /// Vérifie l'existence de plusieurs OUs en batch
        /// </summary>
        public async Task<HashSet<string>> GetOrganizationalUnitsBatchAsync(List<string> ouPaths)
        {
            if (!ouPaths?.Any() == true)
                return new HashSet<string>();

            var existingOus = new HashSet<string>();
            const int batchSize = 50; // Lots plus petits pour les OUs

            try
            {
                if (!await EnsureConnectionAsync())
                {
                    _logger.LogWarning("⚠️ GetOrganizationalUnitsBatchAsync: LDAP indisponible, retour liste vide");
                    return existingOus;
                }

                for (int i = 0; i < ouPaths.Count; i += batchSize)
                {
                    var batch = ouPaths.Skip(i).Take(batchSize).ToList();
                    var batchResults = await GetOrganizationalUnitsBatchInternalAsync(batch);
                    foreach (var ou in batchResults) // Ajouter les résultats au HashSet
                    {
                        existingOus.Add(ou);
                    }
                }

                _logger.LogInformation($"✅ Vérification batch OUs: {existingOus.Count} OUs existantes sur {ouPaths.Count} vérifiées");
                return existingOus;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Erreur lors de la vérification batch de {ouPaths.Count} OUs");
                return existingOus;
            }
        }

        /// <summary>
        /// Requête LDAP interne pour vérifier un lot d'OUs
        /// </summary>
        private async Task<List<string>> GetOrganizationalUnitsBatchInternalAsync(List<string> ouPaths)
        {
            var existingOus = new List<string>();

            return await Task.Run(() =>
            {
                try
                {
                    // Construire un filtre pour rechercher plusieurs OUs par leur DN
                    var ouFilters = ouPaths.Select(ouPath => $"(distinguishedName={EscapeLdapValue(ouPath)})");
                    var combinedFilter = $"(&(objectClass=organizationalUnit)(|{string.Join("", ouFilters)}))";
                    
                    var req = new SearchRequest(_baseDn, combinedFilter, SearchScope.Subtree, new[] { "distinguishedName" });
                    req.SizeLimit = 1000;

                    var res = (SearchResponse)_connection.SendRequest(req);
                    
                    foreach (SearchResultEntry entry in res.Entries)
                    {
                        try
                        {
                            var dn = entry.DistinguishedName;
                            if (!string.IsNullOrEmpty(dn))
                            {
                                existingOus.Add(dn);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Erreur lors de l'extraction du DN d'une OU");
                        }
                    }

                    _logger.LogDebug($"Batch OU interne: {existingOus.Count} OUs trouvées sur {ouPaths.Count} demandées");
                    return existingOus;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Erreur dans GetOrganizationalUnitsBatchInternalAsync pour {ouPaths.Count} OUs");
                    return existingOus;
                }
            });
        }

        #endregion

        #region Méthodes utilitaires pour les opérations batch

        /// <summary>
        /// Échappe les valeurs pour les requêtes LDAP (sécurité)
        /// </summary>
        private string EscapeLdapValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            return value
                .Replace("\\", "\\5c")
                .Replace("*", "\\2a")
                .Replace("(", "\\28")
                .Replace(")", "\\29")
                .Replace("\0", "\\00");
        }

        /// <summary>
        /// Crée un UserModel à partir d'une entrée de résultat de recherche LDAP
        /// </summary>
        private UserModel? CreateUserModelFromSearchResult(SearchResultEntry entry)
        {
            if (entry == null) return null;

            var samAccountName = GetAttributeValue(entry, "sAMAccountName");
            if (string.IsNullOrEmpty(samAccountName)) return null;

            var distinguishedName = entry.DistinguishedName;
            var displayName = GetAttributeValue(entry, "displayName");
            var givenName = GetAttributeValue(entry, "givenName");
            var sn = GetAttributeValue(entry, "sn");
            var mail = GetAttributeValue(entry, "mail");
            var userPrincipalName = GetAttributeValue(entry, "userPrincipalName");

            var additionalAttributes = new Dictionary<string, string>();
            foreach (string attrName in entry.Attributes.AttributeNames)
            {
                if (entry.Attributes[attrName].Count > 0)
                {
                    additionalAttributes[attrName] = entry.Attributes[attrName][0]?.ToString() ?? string.Empty;
                }
            }
            
            return new UserModel
            {
                SamAccountName = samAccountName,
                DistinguishedName = distinguishedName,
                DisplayName = displayName,
                GivenName = givenName,
                Surname = sn,
                UserPrincipalName = userPrincipalName,
                Email = mail, // Assurez-vous que cette propriété existe dans UserModel
                OrganizationalUnit = ExtractOuFromDn(distinguishedName), // Assurez-vous que cette méthode existe
                AdditionalAttributes = additionalAttributes
            };
        }

        /// <summary>
        /// Extrait la valeur d'un attribut depuis un SearchResultEntry
        /// </summary>
        private string GetAttributeValue(SearchResultEntry entry, string attributeName)
        {
            if (entry.Attributes.Contains(attributeName) && entry.Attributes[attributeName].Count > 0)
            {
                return entry.Attributes[attributeName][0]?.ToString() ?? "";
            }
            return "";
        }

        #endregion
    }
    


}
