import React from 'react';
import Label from 'Components/Label';
import VirtualTableRowCell from 'Components/Table/Cells/VirtualTableRowCell';
import Language from 'Language/Language';
import MovieLanguages from 'Movie/MovieLanguages';
import { MovieFile } from 'MovieFile/MovieFile';
import formatBytes from 'Utilities/Number/formatBytes';
import styles from './SelectMovieRow.css';

const languageDisplayNames = new Intl.DisplayNames(['en'], {
  type: 'language',
});
const languageCodeRegex = /^[a-z]{2,3}(?:-[a-z0-9]{2,8})?$/;

interface SelectMovieRowProps {
  title: string;
  tmdbId: number;
  imdbId?: string;
  year: number;
  movieFile?: MovieFile;
  languages: Language[];
  selectedLanguage?: Language;
}

function stopPropagation(event: React.MouseEvent<HTMLAnchorElement>) {
  event.stopPropagation();
}

function splitValues(value?: string) {
  if (!value) {
    return [];
  }

  return value
    .split(/[/,]/)
    .map((item) => item.trim())
    .filter(Boolean);
}

function dedupeValues(values: string[]) {
  return values.filter(
    (value, index) =>
      values.findIndex((item) => item.toLowerCase() === value.toLowerCase()) ===
      index
  );
}

function getLanguageName(value: string) {
  const trimmedValue = value.trim();
  const normalizedValue = trimmedValue.toLowerCase();

  if (!normalizedValue || normalizedValue === 'und') {
    return null;
  }

  if (languageCodeRegex.test(normalizedValue)) {
    try {
      return languageDisplayNames.of(normalizedValue) ?? trimmedValue;
    } catch {
      return trimmedValue;
    }
  }

  return trimmedValue;
}

function getSyntheticLanguageId(value: string) {
  let hash = 0;

  for (let i = 0; i < value.length; i++) {
    hash = (hash * 31 + value.charCodeAt(i)) % 1000000000;
  }

  return -Math.abs(hash || 1);
}

function getLanguage(
  value: string,
  languages: Language[]
): Language | undefined {
  const languageName = getLanguageName(value);

  if (!languageName) {
    return undefined;
  }

  return (
    languages.find(
      (language) => language.name.toLowerCase() === languageName.toLowerCase()
    ) ?? {
      id: getSyntheticLanguageId(languageName),
      name: languageName,
    }
  );
}

function dedupeLanguages(languages: Language[]) {
  return languages.filter(
    (language, index) =>
      languages.findIndex(
        (item) => item.name.toLowerCase() === language.name.toLowerCase()
      ) === index
  );
}

function getLanguageSortPriority(
  language: Language,
  selectedLanguage?: Language
) {
  const languageName = language.name.toLowerCase();

  if (languageName === selectedLanguage?.name.toLowerCase()) {
    return 0;
  }

  if (languageName === 'english') {
    return 1;
  }

  return 2;
}

function sortLanguages(languages: Language[], selectedLanguage?: Language) {
  return languages
    .map((language, index) => ({ language, index }))
    .sort((a, b) => {
      return (
        getLanguageSortPriority(a.language, selectedLanguage) -
          getLanguageSortPriority(b.language, selectedLanguage) ||
        a.index - b.index
      );
    })
    .map(({ language }) => language);
}

function getMediaInfoLanguages(
  values: string[],
  languages: Language[],
  selectedLanguage?: Language
) {
  return sortLanguages(
    dedupeLanguages(
      values
        .map((value) => getLanguage(value, languages))
        .filter((language): language is Language => language != null)
    ),
    selectedLanguage
  );
}

function getFileName(movieFile?: MovieFile) {
  if (!movieFile) {
    return '';
  }

  return movieFile.relativePath || movieFile.sceneName || movieFile.path;
}

function SelectMovieRow({
  title,
  year,
  tmdbId,
  imdbId,
  movieFile,
  languages,
  selectedLanguage,
}: SelectMovieRowProps) {
  const fileName = getFileName(movieFile);
  const mediaAudioLanguages = splitValues(movieFile?.mediaInfo?.audioLanguages);
  const audioLanguages = mediaAudioLanguages.length
    ? getMediaInfoLanguages(mediaAudioLanguages, languages, selectedLanguage)
    : sortLanguages(
        dedupeLanguages(movieFile?.languages ?? []),
        selectedLanguage
      );
  const subtitles = getMediaInfoLanguages(
    dedupeValues(splitValues(movieFile?.mediaInfo?.subtitles)),
    languages,
    selectedLanguage
  );

  return (
    <>
      <VirtualTableRowCell className={styles.title}>
        {title}
      </VirtualTableRowCell>

      <VirtualTableRowCell className={styles.year}>{year}</VirtualTableRowCell>

      <VirtualTableRowCell className={styles.fileName}>
        <span title={fileName}>{fileName}</span>
      </VirtualTableRowCell>

      <VirtualTableRowCell className={styles.size}>
        {movieFile?.size ? formatBytes(movieFile.size) : null}
      </VirtualTableRowCell>

      <VirtualTableRowCell className={styles.audio}>
        <MovieLanguages languages={audioLanguages} />
      </VirtualTableRowCell>

      <VirtualTableRowCell className={styles.subtitles}>
        <MovieLanguages languages={subtitles} />
      </VirtualTableRowCell>

      <VirtualTableRowCell className={styles.imdbId}>
        {imdbId ? (
          <a
            className={styles.externalLink}
            href={`https://www.imdb.com/title/${imdbId}/`}
            rel="noreferrer"
            target="_blank"
            onClick={stopPropagation}
          >
            <Label>{imdbId}</Label>
          </a>
        ) : null}
      </VirtualTableRowCell>

      <VirtualTableRowCell className={styles.tmdbId}>
        <a
          className={styles.externalLink}
          href={`https://www.themoviedb.org/movie/${tmdbId}`}
          rel="noreferrer"
          target="_blank"
          onClick={stopPropagation}
        >
          <Label>{tmdbId}</Label>
        </a>
      </VirtualTableRowCell>
    </>
  );
}

export default SelectMovieRow;
