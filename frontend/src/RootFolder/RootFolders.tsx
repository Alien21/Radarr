import React, { useEffect } from 'react';
import { useDispatch, useSelector } from 'react-redux';
import Alert from 'Components/Alert';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Column from 'Components/Table/Column';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import { kinds } from 'Helpers/Props';
import { fetchRootFolders } from 'Store/Actions/rootFolderActions';
import createRootFoldersSelector from 'Store/Selectors/createRootFoldersSelector';
import translate from 'Utilities/String/translate';
import SetRootFolderForAutoImportCallback from '../typings/SetRootFolderForAutoImportCallback';
import RootFolderRow from './RootFolderRow';

const rootFolderColumns: Column[] = [
  {
    name: 'autoImport',
    label: () => translate('AutoImport'),
    isVisible: true,
  },
  {
    name: 'path',
    label: () => translate('Path'),
    isVisible: true,
  },
  {
    name: 'freeSpace',
    label: () => translate('FreeSpace'),
    isVisible: true,
  },
  {
    name: 'unmappedFolders',
    label: () => translate('UnmappedFolders'),
    isVisible: true,
  },
  {
    name: 'actions',
    label: '',
    isVisible: true,
  },
];

interface RootFoldersProps {
  onSetRootFolderForAutoImportPress: SetRootFolderForAutoImportCallback;
}

function RootFolders(props: RootFoldersProps) {
  const { onSetRootFolderForAutoImportPress } = props;

  const { isFetching, isPopulated, error, items } = useSelector(
    createRootFoldersSelector()
  );

  const dispatch = useDispatch();

  useEffect(() => {
    dispatch(fetchRootFolders());
  }, [dispatch]);

  if (isFetching && !isPopulated) {
    return <LoadingIndicator />;
  }

  if (!isFetching && !!error) {
    return (
      <Alert kind={kinds.DANGER}>{translate('RootFoldersLoadError')}</Alert>
    );
  }

  return (
    <Table columns={rootFolderColumns}>
      <TableBody>
        {items.map((rootFolder) => {
          return (
            <RootFolderRow
              key={rootFolder.id}
              id={rootFolder.id}
              path={rootFolder.path}
              accessible={rootFolder.accessible}
              freeSpace={rootFolder.freeSpace}
              unmappedFolders={rootFolder.unmappedFolders}
              onSetRootFolderForAutoImportPress={
                onSetRootFolderForAutoImportPress
              }
            />
          );
        })}
      </TableBody>
    </Table>
  );
}

export default RootFolders;
