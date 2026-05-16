import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { useDispatch, useSelector } from 'react-redux';
import Icon from 'Components/Icon';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableRowCellButton from 'Components/Table/Cells/TableRowCellButton';
import TableSelectCell from 'Components/Table/Cells/TableSelectCell';
import Column from 'Components/Table/Column';
import TableRow from 'Components/Table/TableRow';
import Popover from 'Components/Tooltip/Popover';
import { icons, kinds, tooltipPositions } from 'Helpers/Props';
import SelectIndexerFlagsModal from 'InteractiveImport/IndexerFlags/SelectIndexerFlagsModal';
import { ExistingMovieFile } from 'InteractiveImport/InteractiveImport';
import SelectLanguageModal from 'InteractiveImport/Language/SelectLanguageModal';
import SelectMovieModal from 'InteractiveImport/Movie/SelectMovieModal';
import SelectQualityModal from 'InteractiveImport/Quality/SelectQualityModal';
import SelectReleaseGroupModal from 'InteractiveImport/ReleaseGroup/SelectReleaseGroupModal';
import Language from 'Language/Language';
import IndexerFlags from 'Movie/IndexerFlags';
import Movie from 'Movie/Movie';
import MovieFormats from 'Movie/MovieFormats';
import MovieLanguages from 'Movie/MovieLanguages';
import MovieQuality from 'Movie/MovieQuality';
import useMovie from 'Movie/useMovie';
import { QualityModel } from 'Quality/Quality';
import {
  reprocessInteractiveImportItems,
  updateInteractiveImportItem,
} from 'Store/Actions/interactiveImportActions';
import createUISettingsSelector from 'Store/Selectors/createUISettingsSelector';
import CustomFormat from 'typings/CustomFormat';
import { SelectStateInputProps } from 'typings/props';
import Rejection from 'typings/Rejection';
import formatBytes from 'Utilities/Number/formatBytes';
import formatCustomFormatScore from 'Utilities/Number/formatCustomFormatScore';
import translate from 'Utilities/String/translate';
import InteractiveImportRowCellPlaceholder from './InteractiveImportRowCellPlaceholder';
import styles from './InteractiveImportRow.css';

type SelectType =
  | 'movie'
  | 'releaseGroup'
  | 'quality'
  | 'language'
  | 'indexerFlags';

type SelectedChangeProps = SelectStateInputProps & {
  hasMovieFileId: boolean;
};

function sortLanguagesByPreference(
  languages: Language[] = [],
  movieInfoLanguage: number
) {
  return languages
    .map((language, index) => ({ language, index }))
    .sort((a, b) => {
      const priorityDiff =
        getLanguagePriority(a.language, movieInfoLanguage) -
        getLanguagePriority(b.language, movieInfoLanguage);

      return priorityDiff || a.index - b.index;
    })
    .map((item) => item.language);
}

function getLanguagePriority(language: Language, movieInfoLanguage: number) {
  if (language.id === movieInfoLanguage) {
    return 0;
  }

  if (language.id === 1) {
    return 1;
  }

  return 2;
}

function getFileName(relativePath: string) {
  return relativePath.split(/[\\/]/).pop() ?? relativePath;
}

interface InteractiveImportRowProps {
  id: number;
  allowMovieChange: boolean;
  relativePath: string;
  movie?: Movie;
  existingMovieFile?: ExistingMovieFile;
  releaseGroup?: string;
  quality?: QualityModel;
  languages?: Language[];
  size: number;
  customFormats?: CustomFormat[];
  customFormatScore?: number;
  indexerFlags: number;
  rejections: Rejection[];
  columns: Column[];
  movieFileId?: number;
  isReprocessing?: boolean;
  isSelected?: boolean;
  modalTitle: string;
  onSelectedChange(result: SelectedChangeProps): void;
  onValidRowChange(id: number, isValid: boolean): void;
}

function InteractiveImportRow(props: InteractiveImportRowProps) {
  const {
    id,
    allowMovieChange,
    relativePath,
    movie,
    existingMovieFile,
    quality,
    languages,
    releaseGroup,
    size,
    customFormats,
    customFormatScore,
    indexerFlags,
    rejections,
    isSelected,
    modalTitle,
    movieFileId,
    columns,
    onSelectedChange,
    onValidRowChange,
  } = props;

  const dispatch = useDispatch();
  const movieFromStore = useMovie(movie?.id);
  const { movieInfoLanguage } = useSelector(createUISettingsSelector());

  const isMovieColumnVisible = useMemo(
    () => columns.find((c) => c.name === 'movie')?.isVisible ?? false,
    [columns]
  );
  const isIndexerFlagsColumnVisible = useMemo(
    () => columns.find((c) => c.name === 'indexerFlags')?.isVisible ?? false,
    [columns]
  );

  const [selectModalOpen, setSelectModalOpen] = useState<SelectType | null>(
    null
  );

  useEffect(
    () => {
      if (allowMovieChange && movie && quality && languages && size > 0) {
        onSelectedChange({
          id,
          hasMovieFileId: !!movieFileId,
          value: true,
          shiftKey: false,
        });
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    []
  );

  useEffect(() => {
    const isValid = !!(movie && quality && languages);

    if (isSelected && !isValid) {
      onValidRowChange(id, false);
    } else {
      onValidRowChange(id, true);
    }
  }, [id, movie, quality, languages, isSelected, onValidRowChange]);

  const handleSelectedChange = useCallback(
    (result: SelectStateInputProps) => {
      onSelectedChange({
        ...result,
        hasMovieFileId: !!movieFileId,
      });
    },
    [movieFileId, onSelectedChange]
  );

  const selectRowAfterChange = useCallback(() => {
    if (!isSelected) {
      onSelectedChange({
        id,
        hasMovieFileId: !!movieFileId,
        value: true,
        shiftKey: false,
      });
    }
  }, [id, movieFileId, isSelected, onSelectedChange]);

  const onSelectModalClose = useCallback(() => {
    setSelectModalOpen(null);
  }, [setSelectModalOpen]);

  const onSelectMoviePress = useCallback(() => {
    setSelectModalOpen('movie');
  }, [setSelectModalOpen]);

  const onMovieSelect = useCallback(
    (movie: Movie) => {
      dispatch(
        updateInteractiveImportItem({
          id,
          movie,
        })
      );

      dispatch(reprocessInteractiveImportItems({ ids: [id] }));

      setSelectModalOpen(null);
      selectRowAfterChange();
    },
    [id, dispatch, setSelectModalOpen, selectRowAfterChange]
  );

  const onSelectReleaseGroupPress = useCallback(() => {
    setSelectModalOpen('releaseGroup');
  }, [setSelectModalOpen]);

  const onReleaseGroupSelect = useCallback(
    (releaseGroup: string) => {
      dispatch(
        updateInteractiveImportItem({
          id,
          releaseGroup,
        })
      );

      dispatch(reprocessInteractiveImportItems({ ids: [id] }));

      setSelectModalOpen(null);
      selectRowAfterChange();
    },
    [id, dispatch, setSelectModalOpen, selectRowAfterChange]
  );

  const onSelectQualityPress = useCallback(() => {
    setSelectModalOpen('quality');
  }, [setSelectModalOpen]);

  const onQualitySelect = useCallback(
    (quality: QualityModel) => {
      dispatch(
        updateInteractiveImportItem({
          id,
          quality,
        })
      );

      dispatch(reprocessInteractiveImportItems({ ids: [id] }));

      setSelectModalOpen(null);
      selectRowAfterChange();
    },
    [id, dispatch, setSelectModalOpen, selectRowAfterChange]
  );

  const onSelectLanguagePress = useCallback(() => {
    setSelectModalOpen('language');
  }, [setSelectModalOpen]);

  const onLanguagesSelect = useCallback(
    (languages: Language[]) => {
      dispatch(
        updateInteractiveImportItem({
          id,
          languages,
        })
      );

      dispatch(reprocessInteractiveImportItems({ ids: [id] }));

      setSelectModalOpen(null);
      selectRowAfterChange();
    },
    [id, dispatch, setSelectModalOpen, selectRowAfterChange]
  );

  const onSelectIndexerFlagsPress = useCallback(() => {
    setSelectModalOpen('indexerFlags');
  }, [setSelectModalOpen]);

  const onIndexerFlagsSelect = useCallback(
    (indexerFlags: number) => {
      dispatch(
        updateInteractiveImportItem({
          id,
          indexerFlags,
        })
      );

      dispatch(reprocessInteractiveImportItems({ ids: [id] }));

      setSelectModalOpen(null);
      selectRowAfterChange();
    },
    [id, dispatch, setSelectModalOpen, selectRowAfterChange]
  );

  const movieTitle = movieFromStore?.title ?? movie?.title ?? '';

  const showMoviePlaceholder = isSelected && !movie;
  const showReleaseGroupPlaceholder = isSelected && !releaseGroup;
  const showQualityPlaceholder = isSelected && !quality;
  const showLanguagePlaceholder = isSelected && !languages;
  const showIndexerFlagsPlaceholder = isSelected && !indexerFlags;
  const sortedLanguages = useMemo(
    () => sortLanguagesByPreference(languages, movieInfoLanguage),
    [languages, movieInfoLanguage]
  );
  const existingFileDetails = useMemo(() => {
    if (!existingMovieFile) {
      return null;
    }

    return {
      ...existingMovieFile,
      fileName: getFileName(existingMovieFile.relativePath),
      sortedLanguages: sortLanguagesByPreference(
        existingMovieFile.languages,
        movieInfoLanguage
      ),
    };
  }, [existingMovieFile, movieInfoLanguage]);

  return (
    <TableRow>
      <TableSelectCell
        id={id}
        isSelected={isSelected}
        onSelectedChange={handleSelectedChange}
      />

      <TableRowCell className={styles.relativePath} title={relativePath}>
        {relativePath}

        {existingFileDetails ? (
          <div
            className={styles.existingFileRelativePath}
            title={existingFileDetails.relativePath}
          >
            {existingFileDetails.fileName}
          </div>
        ) : null}
      </TableRowCell>

      {isMovieColumnVisible ? (
        <TableRowCellButton
          isDisabled={!allowMovieChange}
          title={allowMovieChange ? translate('ClickToChangeMovie') : undefined}
          onPress={onSelectMoviePress}
        >
          {showMoviePlaceholder ? (
            <InteractiveImportRowCellPlaceholder />
          ) : (
            movieTitle
          )}
        </TableRowCellButton>
      ) : null}

      <TableRowCellButton
        title={translate('ClickToChangeReleaseGroup')}
        onPress={onSelectReleaseGroupPress}
      >
        {showReleaseGroupPlaceholder ? (
          <InteractiveImportRowCellPlaceholder isOptional={true} />
        ) : (
          releaseGroup
        )}
      </TableRowCellButton>

      <TableRowCellButton
        className={styles.quality}
        title={translate('ClickToChangeQuality')}
        onPress={onSelectQualityPress}
      >
        {showQualityPlaceholder && <InteractiveImportRowCellPlaceholder />}

        {!showQualityPlaceholder && !!quality && (
          <MovieQuality className={styles.label} quality={quality} />
        )}

        {existingFileDetails ? (
          <div className={styles.existingFileValue}>
            <MovieQuality
              className={styles.existingFileLabel}
              quality={existingFileDetails.quality}
            />
          </div>
        ) : null}
      </TableRowCellButton>

      <TableRowCellButton
        className={styles.languages}
        title={translate('ClickToChangeLanguage')}
        onPress={onSelectLanguagePress}
      >
        {showLanguagePlaceholder && <InteractiveImportRowCellPlaceholder />}

        {!showLanguagePlaceholder && !!languages && (
          <MovieLanguages
            className={styles.label}
            languages={sortedLanguages}
          />
        )}

        {existingFileDetails ? (
          <div className={styles.existingFileValue}>
            <MovieLanguages
              className={styles.existingFileLabel}
              languages={existingFileDetails.sortedLanguages}
            />
          </div>
        ) : null}
      </TableRowCellButton>

      <TableRowCell>
        {formatBytes(size)}

        {existingFileDetails ? (
          <div className={styles.existingFileValue}>
            {formatBytes(existingFileDetails.size)}
          </div>
        ) : null}
      </TableRowCell>

      <TableRowCell>
        {customFormats?.length ? (
          <Popover
            anchor={formatCustomFormatScore(
              customFormatScore,
              customFormats.length
            )}
            title={translate('CustomFormats')}
            body={
              <div className={styles.customFormatTooltip}>
                <MovieFormats formats={customFormats} />
              </div>
            }
            position={tooltipPositions.LEFT}
          />
        ) : null}
      </TableRowCell>

      {isIndexerFlagsColumnVisible ? (
        <TableRowCellButton
          title={translate('ClickToChangeIndexerFlags')}
          onPress={onSelectIndexerFlagsPress}
        >
          {showIndexerFlagsPlaceholder ? (
            <InteractiveImportRowCellPlaceholder isOptional={true} />
          ) : (
            <>
              {indexerFlags ? (
                <Popover
                  anchor={<Icon name={icons.FLAG} />}
                  title={translate('IndexerFlags')}
                  body={<IndexerFlags indexerFlags={indexerFlags} />}
                  position={tooltipPositions.LEFT}
                />
              ) : null}
            </>
          )}
        </TableRowCellButton>
      ) : null}

      <TableRowCell>
        {rejections.length ? (
          <Popover
            anchor={<Icon name={icons.DANGER} kind={kinds.DANGER} />}
            title={translate('ReleaseRejected')}
            body={
              <ul>
                {rejections.map((rejection, index) => {
                  return <li key={index}>{rejection.reason}</li>;
                })}
              </ul>
            }
            position={tooltipPositions.LEFT}
            canFlip={false}
          />
        ) : null}
      </TableRowCell>

      <SelectMovieModal
        isOpen={selectModalOpen === 'movie'}
        modalTitle={modalTitle}
        selectedMovie={movie}
        onMovieSelect={onMovieSelect}
        onModalClose={onSelectModalClose}
      />

      <SelectReleaseGroupModal
        isOpen={selectModalOpen === 'releaseGroup'}
        releaseGroup={releaseGroup ?? ''}
        modalTitle={modalTitle}
        onReleaseGroupSelect={onReleaseGroupSelect}
        onModalClose={onSelectModalClose}
      />

      <SelectQualityModal
        isOpen={selectModalOpen === 'quality'}
        qualityId={quality ? quality.quality.id : 0}
        proper={quality ? quality.revision.version > 1 : false}
        real={quality ? quality.revision.real > 0 : false}
        modalTitle={modalTitle}
        onQualitySelect={onQualitySelect}
        onModalClose={onSelectModalClose}
      />

      <SelectLanguageModal
        isOpen={selectModalOpen === 'language'}
        languageIds={languages ? languages.map((l) => l.id) : []}
        modalTitle={modalTitle}
        onLanguagesSelect={onLanguagesSelect}
        onModalClose={onSelectModalClose}
      />

      <SelectIndexerFlagsModal
        isOpen={selectModalOpen === 'indexerFlags'}
        indexerFlags={indexerFlags ?? 0}
        modalTitle={modalTitle}
        onIndexerFlagsSelect={onIndexerFlagsSelect}
        onModalClose={onSelectModalClose}
      />
    </TableRow>
  );
}

export default InteractiveImportRow;
