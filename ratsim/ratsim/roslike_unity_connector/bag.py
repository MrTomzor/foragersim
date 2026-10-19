import pickle

class MessageBag():
    def __init__(self, filename = None) -> None:
        self.steps = []

        if filename:
            with open(filename, 'rb') as handle:
                self.steps = pickle.load(handle)

    def get_all_msgs(self, step_id):
        pass

    def add_step_msgs(self, topics_and_msgs_dict, whitelist_topics = None):
        if whitelist_topics is None:
            self.steps.append(topics_and_msgs_dict)
        else:
            newdict = {}
            for key in topics_and_msgs_dict.keys():
                if key in whitelist_topics:
                    newdict[key] = topics_and_msgs_dict[key]
            self.steps.append(newdict)

    def save_to_file(self, filepath):
        with open(filepath, 'wb') as handle:
            pickle.dump(self.steps, handle, protocol=pickle.HIGHEST_PROTOCOL)

