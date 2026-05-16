import ModelBase from 'App/ModelBase';
import Language from 'Language/Language';
import Movie from 'Movie/Movie';
import { MovieFile } from 'MovieFile/MovieFile';
import { QualityModel } from 'Quality/Quality';
import CustomFormat from 'typings/CustomFormat';
import Rejection from 'typings/Rejection';

export type ExistingMovieFile = Pick<
  MovieFile,
  'id' | 'relativePath' | 'quality' | 'languages' | 'size'
>;

export interface InteractiveImportCommandOptions {
  path: string;
  folderName: string;
  movieId: number;
  releaseGroup?: string;
  quality: QualityModel;
  languages: Language[];
  indexerFlags: number;
  downloadId?: string;
  movieFileId?: number;
}

interface InteractiveImport extends ModelBase {
  path: string;
  relativePath: string;
  folderName: string;
  name: string;
  size: number;
  releaseGroup: string;
  quality: QualityModel;
  languages: Language[];
  movie?: Movie;
  existingMovieFile?: ExistingMovieFile;
  qualityWeight: number;
  customFormats: CustomFormat[];
  indexerFlags: number;
  rejections: Rejection[];
  movieFileId?: number;
}

export default InteractiveImport;
