using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Services.Data;

/// <summary>
/// Mock implementation of ICatalogService for demo/offline mode.
/// </summary>
public sealed class MockCatalogService : ICatalogService
{
    // Album cover image URLs (from Last.fm CDN - verified working)
    private static readonly string[] AlbumImageUrls =
    [
        "https://lastfm.freetls.fastly.net/i/u/500x500/f304ba0296794c6fc9d0e1cccd194ed0.jpg", // Abbey Road
        "https://lastfm.freetls.fastly.net/i/u/500x500/d4bdd038cacbec705e269edb0fd38419.jpg", // The Dark Side of the Moon
        "https://lastfm.freetls.fastly.net/i/u/500x500/1e6f99756d0342f891d3233ac1283d21.jpg", // Led Zeppelin IV
        "https://lastfm.freetls.fastly.net/i/u/500x500/a15e773a42182a7acfe62701d247e297.jpg", // A Night at the Opera
        "https://lastfm.freetls.fastly.net/i/u/500x500/16c9b96bf0f1edf31c210deca6d57430.jpg", // Hunky Dory
        "https://lastfm.freetls.fastly.net/i/u/500x500/349d64820e124b77cb5275ab03042693.jpg", // Rumours
        "https://lastfm.freetls.fastly.net/i/u/500x500/62d26c6cb4ac4bdccb8f3a2a0fd55421.jpg", // OK Computer
        "https://lastfm.freetls.fastly.net/i/u/500x500/e8693de0a153e609b3eaebb42d62e8be.jpg", // Nevermind
        "https://lastfm.freetls.fastly.net/i/u/500x500/827fbd1bac1d3ed232ec6c95a2526139.jpg", // The Queen Is Dead
        "https://lastfm.freetls.fastly.net/i/u/500x500/3278464e10e38a8119da1d9455681654.jpg", // Funeral
        "https://lastfm.freetls.fastly.net/i/u/500x500/909484b931449e8fc2e4fecca90b7eb5.jpg", // Remain in Light
        "https://lastfm.freetls.fastly.net/i/u/500x500/0c6c868b77a4417f937cf09506099081.jpg", // Unknown Pleasures
        "https://lastfm.freetls.fastly.net/i/u/500x500/99088f450ca5eecffdd08995d53bcf8b.jpg", // The Velvet Underground & Nico
        "https://lastfm.freetls.fastly.net/i/u/500x500/6cf55efba65b9f89db4e2754694c0b0e.jpg", // Doolittle
        "https://lastfm.freetls.fastly.net/i/u/500x500/d95051e07a714889c8f7fbbccf61bf8b.jpg", // In the Aeroplane Over the Sea
        "https://lastfm.freetls.fastly.net/i/u/500x500/510546e3b6df7504392274c528c77780.jpg", // Loveless
        "https://lastfm.freetls.fastly.net/i/u/500x500/da3687c17718278341e5d5f28a7aac74.jpg", // Daydream Nation
        "https://lastfm.freetls.fastly.net/i/u/500x500/515b7450118c4ff0b8d0a9ad2b4375ec.jpg", // Crooked Rain, Crooked Rain
        "https://lastfm.freetls.fastly.net/i/u/500x500/a7f76fcb56c94a51ca3eefed472e88b4.jpg", // Marquee Moon
        "https://lastfm.freetls.fastly.net/i/u/500x500/1340e9e1082cf0dc748583b7eefce6d5.jpg", // Discovery
        "https://lastfm.freetls.fastly.net/i/u/500x500/86b35c4eb3c479da49c915d8771bbd1a.jpg", // To Pimp a Butterfly
        "https://lastfm.freetls.fastly.net/i/u/500x500/82c92f044b27db86328ed6be3f8a735a.jpg", // Blonde
        "https://lastfm.freetls.fastly.net/i/u/500x500/e150fa362c89b8f1d92d883ae828b7ef.jpg", // IGOR
        "https://lastfm.freetls.fastly.net/i/u/500x500/dd45b0438a315aed98b5830aa2fc43c5.jpg" // Currents
    ];

    private readonly Dictionary<string, AlbumDetailDto> _mockAlbums;
    private readonly Dictionary<string, List<AlbumTrackDto>> _mockAlbumTracks;

    public MockCatalogService()
    {
        (_mockAlbums, _mockAlbumTracks) = GenerateMockAlbums();
    }

    public Task<AlbumDetailDto> GetAlbumAsync(string albumId, CancellationToken ct = default)
    {
        if (_mockAlbums.TryGetValue(albumId, out var album))
        {
            return Task.FromResult(album);
        }

        // Return a placeholder for unknown albums
        return Task.FromResult(new AlbumDetailDto
        {
            Id = albumId,
            Name = "Unknown Album",
            ArtistId = "unknown",
            ArtistName = "Unknown Artist",
            Year = 2000,
            AlbumType = "Album",
            TrackCount = 0,
            IsSaved = false
        });
    }

    public Task<IReadOnlyList<AlbumTrackDto>> GetAlbumTracksAsync(string albumId, CancellationToken ct = default)
    {
        if (_mockAlbumTracks.TryGetValue(albumId, out var tracks))
        {
            return Task.FromResult<IReadOnlyList<AlbumTrackDto>>(tracks);
        }
        return Task.FromResult<IReadOnlyList<AlbumTrackDto>>([]);
    }

    private static (Dictionary<string, AlbumDetailDto>, Dictionary<string, List<AlbumTrackDto>>) GenerateMockAlbums()
    {
        var albums = new Dictionary<string, AlbumDetailDto>();
        var albumTracks = new Dictionary<string, List<AlbumTrackDto>>();

        var albumData = new[]
        {
            ("The Beatles", "spotify:artist:1", "Abbey Road", 1969, new[] { "Come Together", "Something", "Maxwell's Silver Hammer", "Oh! Darling", "Octopus's Garden", "I Want You (She's So Heavy)", "Here Comes the Sun", "Because", "You Never Give Me Your Money", "Sun King", "Mean Mr. Mustard", "Polythene Pam", "She Came In Through the Bathroom Window", "Golden Slumbers", "Carry That Weight", "The End", "Her Majesty" }),
            ("Pink Floyd", "spotify:artist:2", "The Dark Side of the Moon", 1973, new[] { "Speak to Me", "Breathe", "On the Run", "Time", "The Great Gig in the Sky", "Money", "Us and Them", "Any Colour You Like", "Brain Damage", "Eclipse" }),
            ("Led Zeppelin", "spotify:artist:3", "Led Zeppelin IV", 1971, new[] { "Black Dog", "Rock and Roll", "The Battle of Evermore", "Stairway to Heaven", "Misty Mountain Hop", "Four Sticks", "Going to California", "When the Levee Breaks" }),
            ("Queen", "spotify:artist:4", "A Night at the Opera", 1975, new[] { "Death on Two Legs", "Lazing on a Sunday Afternoon", "I'm in Love with My Car", "You're My Best Friend", "'39", "Sweet Lady", "Seaside Rendezvous", "The Prophet's Song", "Love of My Life", "Good Company", "Bohemian Rhapsody", "God Save the Queen" }),
            ("David Bowie", "spotify:artist:5", "Hunky Dory", 1971, new[] { "Changes", "Oh! You Pretty Things", "Eight Line Poem", "Life on Mars?", "Kooks", "Quicksand", "Fill Your Heart", "Andy Warhol", "Song for Bob Dylan", "Queen Bitch", "The Bewlay Brothers" }),
            ("Fleetwood Mac", "spotify:artist:6", "Rumours", 1977, new[] { "Second Hand News", "Dreams", "Never Going Back Again", "Don't Stop", "Go Your Own Way", "Songbird", "The Chain", "You Make Loving Fun", "I Don't Want to Know", "Oh Daddy", "Gold Dust Woman" }),
            ("Radiohead", "spotify:artist:7", "OK Computer", 1997, new[] { "Airbag", "Paranoid Android", "Subterranean Homesick Alien", "Exit Music (For a Film)", "Let Down", "Karma Police", "Fitter Happier", "Electioneering", "Climbing Up the Walls", "No Surprises", "Lucky", "The Tourist" }),
            ("Nirvana", "spotify:artist:8", "Nevermind", 1991, new[] { "Smells Like Teen Spirit", "In Bloom", "Come as You Are", "Breed", "Lithium", "Polly", "Territorial Pissings", "Drain You", "Lounge Act", "Stay Away", "On a Plain", "Something in the Way" }),
            ("The Smiths", "spotify:artist:9", "The Queen Is Dead", 1986, new[] { "The Queen Is Dead", "Frankly, Mr. Shankly", "I Know It's Over", "Never Had No One Ever", "Cemetry Gates", "Bigmouth Strikes Again", "The Boy with the Thorn in His Side", "Vicar in a Tutu", "There Is a Light That Never Goes Out", "Some Girls Are Bigger Than Others" }),
            ("Arcade Fire", "spotify:artist:10", "Funeral", 2004, new[] { "Neighborhood #1 (Tunnels)", "Neighborhood #2 (Laïka)", "Une Année Sans Lumière", "Neighborhood #3 (Power Out)", "Neighborhood #4 (7 Kettles)", "Crown of Love", "Wake Up", "Haïti", "Rebellion (Lies)", "In the Backseat" }),
            ("Talking Heads", "spotify:artist:11", "Remain in Light", 1980, new[] { "Born Under Punches", "Crosseyed and Painless", "The Great Curve", "Once in a Lifetime", "Houses in Motion", "Seen and Not Seen", "Listening Wind", "The Overload" }),
            ("Joy Division", "spotify:artist:12", "Unknown Pleasures", 1979, new[] { "Disorder", "Day of the Lords", "Candidate", "Insight", "New Dawn Fades", "She's Lost Control", "Shadowplay", "Wilderness", "Interzone", "I Remember Nothing" }),
            ("The Velvet Underground", "spotify:artist:13", "The Velvet Underground & Nico", 1967, new[] { "Sunday Morning", "I'm Waiting for the Man", "Femme Fatale", "Venus in Furs", "Run Run Run", "All Tomorrow's Parties", "Heroin", "There She Goes Again", "I'll Be Your Mirror", "The Black Angel's Death Song", "European Son" }),
            ("Pixies", "spotify:artist:14", "Doolittle", 1989, new[] { "Debaser", "Tame", "Wave of Mutilation", "I Bleed", "Here Comes Your Man", "Dead", "Monkey Gone to Heaven", "Mr. Grieves", "Crackity Jones", "La La Love You", "No. 13 Baby", "There Goes My Gun", "Hey", "Silver", "Gouge Away" }),
            ("Neutral Milk Hotel", "spotify:artist:15", "In the Aeroplane Over the Sea", 1998, new[] { "The King of Carrot Flowers Pt. One", "The King of Carrot Flowers Pts. Two & Three", "In the Aeroplane Over the Sea", "Two-Headed Boy", "The Fool", "Holland, 1945", "Communist Daughter", "Oh Comely", "Ghost", "Untitled", "Two-Headed Boy Pt. Two" }),
            ("My Bloody Valentine", "spotify:artist:16", "Loveless", 1991, new[] { "Only Shallow", "Loomer", "Touched", "To Here Knows When", "When You Sleep", "I Only Said", "Come in Alone", "Sometimes", "Blown a Wish", "What You Want", "Soon" }),
            ("Sonic Youth", "spotify:artist:17", "Daydream Nation", 1988, new[] { "'Teen Age Riot", "Silver Rocket", "The Sprawl", "Cross the Breeze", "Eric's Trip", "Total Trash", "Hey Joni", "Providence", "Candle", "Rain King", "Kissability", "Trilogy: The Wonder / Hyperstation / Eliminator Jr." }),
            ("Pavement", "spotify:artist:18", "Crooked Rain, Crooked Rain", 1994, new[] { "Silence Kit", "Elevate Me Later", "Stop Breathin", "Cut Your Hair", "Newark Wilder", "Unfair", "Gold Soundz", "5-4=Unity", "Range Life", "Heaven Is a Truck", "Hit the Plane Down", "Fillmore Jive" }),
            ("Television", "spotify:artist:19", "Marquee Moon", 1977, new[] { "See No Evil", "Venus", "Friction", "Marquee Moon", "Elevation", "Guiding Light", "Prove It", "Torn Curtain" }),
            ("Daft Punk", "spotify:artist:20", "Discovery", 2001, new[] { "One More Time", "Aerodynamic", "Digital Love", "Harder, Better, Faster, Stronger", "Crescendolls", "Nightvision", "Superheroes", "High Life", "Something About Us", "Voyager", "Veridis Quo", "Short Circuit", "Face to Face", "Too Long" }),
            ("Kendrick Lamar", "spotify:artist:21", "To Pimp a Butterfly", 2015, new[] { "Wesley's Theory", "For Free? (Interlude)", "King Kunta", "Institutionalized", "These Walls", "u", "Alright", "For Sale? (Interlude)", "Momma", "Hood Politics", "How Much a Dollar Cost", "Complexion (A Zulu Love)", "The Blacker the Berry", "You Ain't Gotta Lie (Momma Said)", "i", "Mortal Man" }),
            ("Frank Ocean", "spotify:artist:22", "Blonde", 2016, new[] { "Nikes", "Ivy", "Pink + White", "Be Yourself", "Solo", "Skyline To", "Self Control", "Good Guy", "Nights", "Solo (Reprise)", "Pretty Sweet", "Facebook Story", "Close to You", "White Ferrari", "Seigfried", "Godspeed", "Futura Free" }),
            ("Tyler, the Creator", "spotify:artist:23", "IGOR", 2019, new[] { "IGOR'S THEME", "EARFQUAKE", "I THINK", "EXACTLY WHAT YOU RUN FROM YOU END UP CHASING", "RUNNING OUT OF TIME", "NEW MAGIC WAND", "A BOY IS A GUN*", "PUPPET", "WHAT'S GOOD", "GONE, GONE / THANK YOU", "I DON'T LOVE YOU ANYMORE", "ARE WE STILL FRIENDS?" }),
            ("Tame Impala", "spotify:artist:24", "Currents", 2015, new[] { "Let It Happen", "Nangs", "The Moment", "Yes I'm Changing", "Eventually", "Gossip", "The Less I Know the Better", "Past Life", "Disciples", "Cause I'm a Man", "'Cause I'm a Man", "Reality in Motion", "Love/Paranoia", "New Person, Same Old Mistakes" })
        };

        var albumTypes = new[] { "Album", "Album", "Album", "Single", "EP" };

        for (int i = 0; i < albumData.Length; i++)
        {
            var (artist, artistId, albumName, year, trackNames) = albumData[i];
            var albumId = $"spotify:album:{i + 1}";

            var imageUrl = i < AlbumImageUrls.Length ? AlbumImageUrls[i] : null;

            albums[albumId] = new AlbumDetailDto
            {
                Id = albumId,
                Name = albumName,
                ImageUrl = imageUrl,
                ArtistId = artistId,
                ArtistName = artist,
                Year = year,
                AlbumType = albumTypes[i % albumTypes.Length],
                TrackCount = trackNames.Length,
                IsSaved = i % 3 == 0 // Some albums are saved
            };

            var tracks = new List<AlbumTrackDto>();
            for (int t = 0; t < trackNames.Length; t++)
            {
                tracks.Add(new AlbumTrackDto
                {
                    Id = $"spotify:track:{albumId}:{t + 1}",
                    Title = trackNames[t],
                    ArtistName = artist,
                    ArtistId = artistId,
                    AlbumName = albumName,
                    AlbumId = albumId,
                    ImageUrl = imageUrl,
                    Duration = TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds((t * 17) % 180),
                    IsExplicit = t % 7 == 0,
                    TrackNumber = t + 1,
                    DiscNumber = 1,
                    IsPlayable = true
                });
            }
            albumTracks[albumId] = tracks;
        }

        return (albums, albumTracks);
    }
}
