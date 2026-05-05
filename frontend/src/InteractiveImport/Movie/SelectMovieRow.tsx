import React from 'react';
import Label from 'Components/Label';
import VirtualTableRowCell from 'Components/Table/Cells/VirtualTableRowCell';
import Language from 'Language/Language';
import { MovieFile } from 'MovieFile/MovieFile';
import formatBytes from 'Utilities/Number/formatBytes';
import styles from './SelectMovieRow.css';

interface SelectMovieRowProps {
  title: string;
  tmdbId: number;
  imdbId?: string;
  year: number;
  movieFile?: MovieFile;
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
    .split('/')
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

function sortLanguages(values: string[], selectedLanguage?: Language) {
  const selectedLanguageName = selectedLanguage?.name.toLowerCase();

  const getScore = (value: string) => {
    const normalizedValue = value.toLowerCase();

    if (normalizedValue === selectedLanguageName) {
      return 0;
    }

    if (normalizedValue === 'english') {
      return 1;
    }

    return 2;
  };

  return [...values].sort((a, b) => {
    return getScore(a) - getScore(b);
  });
}

function getDisplayedValues(values: string[]) {
  if (values.length <= 2) {
    return values.join(', ');
  }

  return `${values.slice(0, 2).join(', ')} ...`;
}

function ValueList({ values }: { values: string[] }) {
  if (!values.length) {
    return null;
  }

  return (
    <span className={styles.valueList} title={values.join(', ')}>
      {getDisplayedValues(values)}
    </span>
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
  selectedLanguage,
}: SelectMovieRowProps) {
  const fileName = getFileName(movieFile);
  const mediaAudioLanguages = splitValues(movieFile?.mediaInfo?.audioLanguages);
  const audioLanguages = sortLanguages(
    dedupeValues(
      mediaAudioLanguages.length
        ? mediaAudioLanguages
        : movieFile?.languages.map((language) => language.name) ?? []
    ),
    selectedLanguage
  );
  const subtitles = sortLanguages(
    dedupeValues(splitValues(movieFile?.mediaInfo?.subtitles)),
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
        <ValueList values={audioLanguages} />
      </VirtualTableRowCell>

      <VirtualTableRowCell className={styles.subtitles}>
        <ValueList values={subtitles} />
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
