using System;
using System.Collections.Generic;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.MovieImport
{
    public interface IDualAudioImportPreference
    {
        DualAudioImportPreferenceResult Evaluate(LocalMovie localMovie, MovieFile existingMovieFile);
    }

    public class DualAudioImportPreferenceResult
    {
        public bool Applies { get; set; }
        public bool IsPreferredUpgrade { get; set; }
        public bool RequiresManualReview { get; set; }
        public string ManualReviewReason { get; set; }

        public static DualAudioImportPreferenceResult None()
        {
            return new DualAudioImportPreferenceResult();
        }
    }

    public class DualAudioImportPreference : IDualAudioImportPreference
    {
        private const double MinimumPreferredSizeRatio = 0.70;

        private readonly IConfigService _configService;

        public DualAudioImportPreference(IConfigService configService)
        {
            _configService = configService;
        }

        public DualAudioImportPreferenceResult Evaluate(LocalMovie localMovie, MovieFile existingMovieFile)
        {
            if (!_configService.PreferDualAudio ||
                localMovie?.Movie == null ||
                existingMovieFile == null)
            {
                return DualAudioImportPreferenceResult.None();
            }

            var preferredLanguage = (Language)_configService.MovieInfoLanguage;

            if (!IsKnownLanguage(preferredLanguage))
            {
                return DualAudioImportPreferenceResult.None();
            }

            var candidateLanguages = GetAudioLanguages(localMovie.MediaInfo, localMovie.Languages);
            var existingLanguages = GetAudioLanguages(existingMovieFile.MediaInfo, existingMovieFile.Languages);

            if (!HasPreferredDualAudio(candidateLanguages, preferredLanguage) ||
                HasPreferredDualAudio(existingLanguages, preferredLanguage))
            {
                return DualAudioImportPreferenceResult.None();
            }

            var result = new DualAudioImportPreferenceResult
            {
                Applies = true
            };

            var qualityCompare = CompareQuality(localMovie, existingMovieFile);
            if (qualityCompare < 0)
            {
                result.RequiresManualReview = true;
                result.ManualReviewReason = "Dual-audio candidate is lower quality than the existing movie file; manual review required.";

                return result;
            }

            if (IsMoreThanThirtyPercentSmaller(localMovie.Size, existingMovieFile.Size))
            {
                result.RequiresManualReview = true;
                result.ManualReviewReason = "Dual-audio candidate is more than 30% smaller than the existing movie file; manual review required.";

                return result;
            }

            result.IsPreferredUpgrade = true;

            return result;
        }

        private int CompareQuality(LocalMovie localMovie, MovieFile existingMovieFile)
        {
            if (localMovie.Movie.QualityProfile == null ||
                localMovie.Quality?.Quality == null ||
                existingMovieFile.Quality?.Quality == null)
            {
                return 0;
            }

            var qualityComparer = new QualityModelComparer(localMovie.Movie.QualityProfile);

            return qualityComparer.Compare(localMovie.Quality.Quality, existingMovieFile.Quality.Quality);
        }

        private static bool IsMoreThanThirtyPercentSmaller(long candidateSize, long existingSize)
        {
            if (candidateSize <= 0 || existingSize <= 0)
            {
                return false;
            }

            return candidateSize < existingSize * MinimumPreferredSizeRatio;
        }

        private static bool HasPreferredDualAudio(AudioLanguageSet audioLanguages, Language preferredLanguage)
        {
            return audioLanguages.KnownLanguages.Contains(preferredLanguage) &&
                   audioLanguages.DistinctAudioLanguages.Count > 1;
        }

        private static AudioLanguageSet GetAudioLanguages(MediaInfoModel mediaInfo, List<Language> parsedLanguages)
        {
            var languages = new AudioLanguageSet();

            foreach (var audioLanguage in mediaInfo?.AudioLanguages ?? new List<string>())
            {
                AddRawLanguage(languages, audioLanguage);
            }

            foreach (var language in parsedLanguages ?? new List<Language>())
            {
                AddKnownLanguage(languages, language);
            }

            return languages;
        }

        private static void AddRawLanguage(AudioLanguageSet languages, string rawLanguage)
        {
            if (rawLanguage.IsNullOrWhiteSpace())
            {
                return;
            }

            var language = ParseLanguage(rawLanguage);

            if (IsKnownLanguage(language))
            {
                AddKnownLanguage(languages, language);
                return;
            }

            languages.DistinctAudioLanguages.Add(rawLanguage.Trim().ToLowerInvariant());
        }

        private static void AddKnownLanguage(AudioLanguageSet languages, Language language)
        {
            if (!IsKnownLanguage(language))
            {
                return;
            }

            languages.KnownLanguages.Add(language);
            languages.DistinctAudioLanguages.Add($"language:{language.Id}");
        }

        private static Language ParseLanguage(string rawLanguage)
        {
            var trimmedLanguage = rawLanguage.Trim();

            return IsoLanguages.Find(trimmedLanguage)?.Language ??
                   IsoLanguages.FindByName(trimmedLanguage)?.Language;
        }

        private static bool IsKnownLanguage(Language language)
        {
            return language is { Id: > 0 };
        }

        private class AudioLanguageSet
        {
            public HashSet<Language> KnownLanguages { get; } = new HashSet<Language>();
            public HashSet<string> DistinctAudioLanguages { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
