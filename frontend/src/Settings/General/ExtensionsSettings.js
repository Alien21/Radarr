import PropTypes from 'prop-types';
import React from 'react';
import FieldSet from 'Components/FieldSet';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import { inputTypes, sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';

function ExtensionsSettings(props) {
  const { settings, onInputChange } = props;

  const {
    blockAutoImportForExistingMovieFiles,
    analyzeCompletedDownloadFiles
  } = settings;

  return (
    <FieldSet legend={translate('Extensions')}>
      <FormGroup size={sizes.MEDIUM}>
        <FormLabel>{translate('PreserveDownloadsForExistingMovies')}</FormLabel>

        <FormInputGroup
          type={inputTypes.CHECK}
          name="blockAutoImportForExistingMovieFiles"
          helpText={translate('PreserveDownloadsForExistingMoviesHelpText')}
          onChange={onInputChange}
          {...blockAutoImportForExistingMovieFiles}
        />
      </FormGroup>

      <FormGroup size={sizes.MEDIUM}>
        <FormLabel>{translate('AnalyzeCompletedDownloadFiles')}</FormLabel>

        <FormInputGroup
          type={inputTypes.CHECK}
          name="analyzeCompletedDownloadFiles"
          helpText={translate('AnalyzeCompletedDownloadFilesHelpText')}
          onChange={onInputChange}
          {...analyzeCompletedDownloadFiles}
        />
      </FormGroup>
    </FieldSet>
  );
}

ExtensionsSettings.propTypes = {
  settings: PropTypes.object.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default ExtensionsSettings;
