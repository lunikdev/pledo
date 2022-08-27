﻿using Plex.ServerApi.PlexModels.Account;
using Web.Models;
using Web.Models.DTO;

namespace Web.Services;

public interface IPlexService
{
    Task<PlexAccount?> LoginAccount(Credentials credentials);
    Task<IEnumerable<Server>> RetrieveServers(Account account);
    Task<IEnumerable<Library>> RetrieveLibraries(Server server);
    Task<Movie> RetrieveMovieByKey(Library library, string movieKey);
    Task<IEnumerable<Movie>> RetrieveMovies(Library library);
    Task<string> GetUriFromFastestConnection(Server server);
}