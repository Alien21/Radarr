import React from 'react';
import FieldSet from 'Components/FieldSet';
import translate from 'Utilities/String/translate';

function ExtensionsSettings() {
  return (
    <FieldSet legend={translate('Extensions')} />
  );
}

export default ExtensionsSettings;
