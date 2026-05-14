import jdu from 'jdu';
import { throttle } from 'lodash';
import React, {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { useSelector } from 'react-redux';
import { FixedSizeList as List, ListChildComponentProps } from 'react-window';
import TextInput from 'Components/Form/TextInput';
import Button from 'Components/Link/Button';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import Scroller from 'Components/Scroller/Scroller';
import Column from 'Components/Table/Column';
import VirtualTableRowButton from 'Components/Table/VirtualTableRowButton';
import { scrollDirections } from 'Helpers/Props';
import Language from 'Language/Language';
import Movie from 'Movie/Movie';
import createAllMoviesSelector from 'Store/Selectors/createAllMoviesSelector';
import createLanguagesSelector from 'Store/Selectors/createLanguagesSelector';
import createUISettingsSelector from 'Store/Selectors/createUISettingsSelector';
import dimensions from 'Styles/Variables/dimensions';
import { InputChanged } from 'typings/inputs';
import sortByProp from 'Utilities/Array/sortByProp';
import translate from 'Utilities/String/translate';
import SelectMovieModalTableHeader from './SelectMovieModalTableHeader';
import SelectMovieRow from './SelectMovieRow';
import styles from './SelectMovieModalContent.css';

const columns = [
  {
    name: 'title',
    label: () => translate('Title'),
    isVisible: true,
  },
  {
    name: 'year',
    label: () => translate('Year'),
    isVisible: true,
  },
  {
    name: 'fileName',
    label: () => translate('File'),
    isVisible: true,
  },
  {
    name: 'size',
    label: () => translate('Size'),
    isVisible: true,
  },
  {
    name: 'audio',
    label: () => 'Audio',
    isVisible: true,
  },
  {
    name: 'subtitles',
    label: () => 'Subtitles',
    isVisible: true,
  },
  {
    name: 'imdbId',
    label: () => translate('IMDbId'),
    isVisible: true,
  },
  {
    name: 'tmdbId',
    label: () => translate('TMDBId'),
    isVisible: true,
  },
];

const bodyPadding = parseInt(dimensions.pageContentBodyPadding);

function normalizeMovieFilterValue(value: string | undefined | null) {
  return jdu
    .replace(value ?? '')
    .toLowerCase()
    .replace(/[^\p{L}\p{N}]+/gu, '')
    .trim();
}

interface SelectMovieModalContentProps {
  modalTitle: string;
  onMovieSelect(movie: Movie): void;
  onModalClose(): void;
}

interface RowItemData {
  items: Movie[];
  columns: Column[];
  languages: Language[];
  selectedLanguage?: Language;
  onMovieSelect(movieId: number): void;
}

function Row({ index, style, data }: ListChildComponentProps<RowItemData>) {
  const { items, languages, selectedLanguage, onMovieSelect } = data;
  const movie = index >= items.length ? null : items[index];

  const handlePress = useCallback(() => {
    if (movie?.id) {
      onMovieSelect(movie.id);
    }
  }, [movie?.id, onMovieSelect]);

  if (movie == null) {
    return null;
  }

  return (
    <VirtualTableRowButton
      style={{
        display: 'flex',
        justifyContent: 'space-between',
        ...style,
      }}
      onPress={handlePress}
    >
      <SelectMovieRow
        key={movie.id}
        title={movie.title}
        tmdbId={movie.tmdbId}
        imdbId={movie.imdbId}
        year={movie.year}
        movieFile={movie.movieFile}
        languages={languages}
        selectedLanguage={selectedLanguage}
      />
    </VirtualTableRowButton>
  );
}

function SelectMovieModalContent(props: SelectMovieModalContentProps) {
  const { modalTitle, onMovieSelect, onModalClose } = props;

  const listRef = useRef<List<RowItemData>>(null);
  const scrollerRef = useRef<HTMLDivElement>(null);
  const allMovies: Movie[] = useSelector(createAllMoviesSelector());
  const { movieInfoLanguage } = useSelector(createUISettingsSelector());
  const { items: languages } = useSelector(createLanguagesSelector());
  const selectedLanguage = languages.find(
    (language) => language.id === movieInfoLanguage
  );
  const [filter, setFilter] = useState('');
  const [size, setSize] = useState({ width: 0, height: 0 });
  const windowHeight = window.innerHeight;

  useEffect(() => {
    const current = scrollerRef?.current as HTMLElement;

    if (current) {
      const width = current.clientWidth;
      const height = current.clientHeight;
      const padding = bodyPadding - 5;

      setSize({
        width: width - padding * 2,
        height: height + padding,
      });
    }
  }, [windowHeight, scrollerRef]);

  useEffect(() => {
    const currentScrollerRef = scrollerRef.current as HTMLElement;
    const currentScrollListener = currentScrollerRef;

    const handleScroll = throttle(() => {
      const { offsetTop = 0 } = currentScrollerRef;
      const scrollTop = currentScrollerRef.scrollTop - offsetTop;

      listRef.current?.scrollTo(scrollTop);
    }, 10);

    currentScrollListener.addEventListener('scroll', handleScroll);

    return () => {
      handleScroll.cancel();

      if (currentScrollListener) {
        currentScrollListener.removeEventListener('scroll', handleScroll);
      }
    };
  }, [listRef, scrollerRef]);

  const onFilterChange = useCallback(
    ({ value }: InputChanged<string>) => {
      setFilter(value);
    },
    [setFilter]
  );

  const onMovieSelectWrapper = useCallback(
    (movieId: number) => {
      const movie = allMovies.find((s) => s.id === movieId) as Movie;

      onMovieSelect(movie);
    },
    [allMovies, onMovieSelect]
  );

  const sortedMovies = useMemo(
    () => [...allMovies].sort(sortByProp('sortTitle')),
    [allMovies]
  );

  const items = useMemo(() => {
    const filterValue = normalizeMovieFilterValue(filter);
    const idFilterValue = filter.trim().toLowerCase();

    if (!filterValue && !idFilterValue) {
      return sortedMovies;
    }

    return sortedMovies.filter((item) => {
      const movieTitles = [
        item.title,
        item.originalTitle,
        ...(item.alternateTitles ?? []).map(({ title }) => title),
      ];

      return (
        movieTitles.some((title) =>
          normalizeMovieFilterValue(title).includes(filterValue)
        ) ||
        item.tmdbId.toString().includes(idFilterValue) ||
        item.imdbId?.toLowerCase().includes(idFilterValue)
      );
    });
  }, [sortedMovies, filter]);

  return (
    <ModalContent onModalClose={onModalClose}>
      <ModalHeader>
        {translate('SelectMovieModalTitle', { modalTitle })}
      </ModalHeader>

      <ModalBody
        className={styles.modalBody}
        scrollDirection={scrollDirections.NONE}
      >
        <TextInput
          className={styles.filterInput}
          placeholder={translate('FilterMoviePlaceholder')}
          name="filter"
          value={filter}
          autoFocus={true}
          onChange={onFilterChange}
        />

        <Scroller
          ref={scrollerRef}
          className={styles.scroller}
          autoFocus={false}
        >
          <SelectMovieModalTableHeader columns={columns} />
          <List<RowItemData>
            ref={listRef}
            style={{
              width: '100%',
              height: '100%',
              overflow: 'none',
            }}
            width={size.width}
            height={size.height}
            itemCount={items.length}
            itemSize={38}
            itemData={{
              items,
              columns,
              onMovieSelect: onMovieSelectWrapper,
              languages,
              selectedLanguage,
            }}
          >
            {Row}
          </List>
        </Scroller>
      </ModalBody>

      <ModalFooter>
        <Button onPress={onModalClose}>{translate('Cancel')}</Button>
      </ModalFooter>
    </ModalContent>
  );
}

export default SelectMovieModalContent;
