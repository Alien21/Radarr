import React from 'react';
import Label from 'Components/Label';
import VirtualTableRowCell from 'Components/Table/Cells/VirtualTableRowCell';
import styles from './SelectMovieRow.css';

interface SelectMovieRowProps {
  title: string;
  tmdbId: number;
  imdbId?: string;
  year: number;
}

function stopPropagation(event: React.MouseEvent<HTMLAnchorElement>) {
  event.stopPropagation();
}

function SelectMovieRow({ title, year, tmdbId, imdbId }: SelectMovieRowProps) {
  return (
    <>
      <VirtualTableRowCell className={styles.title}>
        {title}
      </VirtualTableRowCell>

      <VirtualTableRowCell className={styles.year}>{year}</VirtualTableRowCell>

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
